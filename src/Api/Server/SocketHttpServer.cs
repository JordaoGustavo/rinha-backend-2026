using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rinha.Api;

public sealed class SocketHttpServer : IDisposable
{
    private readonly Socket _listener;
    private readonly IFraudDetector _detector;
    private readonly HttpResponseTable _responses;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _serverTimingEnabled;
    private readonly bool _fdPassing;
    private static readonly double TickToUs = 1_000_000.0 / Stopwatch.Frequency;

    private readonly Socket? _ctrlListener;

    private const int MaxFd = 4096;
    private const int ConnBufSize = 4096;
    private readonly byte[][] _connBuf = new byte[MaxFd][];
    private readonly int[] _connFilled = new int[MaxFd];
    private readonly Stack<byte[]> _bufPool = new();

    public SocketHttpServer(string socketPath, IFraudDetector detector, HttpResponseTable responses, int backlog = 2048)
    {
        _detector = detector;
        _responses = responses;
        _serverTimingEnabled = Environment.GetEnvironmentVariable("SERVER_TIMING") == "1";
        _fdPassing = Environment.GetEnvironmentVariable("FD_PASSING") == "1";
        if (File.Exists(socketPath)) File.Delete(socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(backlog);
        SetSocketPerms(socketPath);

        if (_fdPassing)
        {
            string ctrlPath = socketPath + ".ctrl";
            if (File.Exists(ctrlPath)) File.Delete(ctrlPath);
            _ctrlListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _ctrlListener.Bind(new UnixDomainSocketEndPoint(ctrlPath));
            _ctrlListener.Listen(16);
            SetSocketPerms(ctrlPath);
        }
    }

    private static void SetSocketPerms(string path)
    {
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
    }

    public Task RunAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptThread = new Thread(() =>
        {
            try { AcceptLoop(); }
            finally { tcs.TrySetResult(); }
        })
        {
            IsBackground = true,
            Name = "accept-loop"
        };
        acceptThread.Start();
        return tcs.Task;
    }

    private void AcceptLoop()
    {
        if (_fdPassing)
        {
            AcceptCtrlLoop();
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            Socket conn;
            try { conn = _listener.Accept(); }
            catch { break; }

            var thread = new Thread(HandleConnectionSync)
            {
                IsBackground = true,
                Name = "conn-handler"
            };
            thread.Start(conn);
        }
    }

    private void AcceptCtrlLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket ctrlConn;
            try { ctrlConn = _ctrlListener!.Accept(); }
            catch { break; }

            RunEpollLoop((int)ctrlConn.Handle);
            GC.KeepAlive(ctrlConn);
            try { ctrlConn.Close(); } catch { }
        }
    }

    private unsafe void RunEpollLoop(int ctrlFd)
    {
        int epfd = EpollInterop.epoll_create1(0);
        if (epfd < 0) return;

        EpollInterop.SetNonBlocking(ctrlFd);
        var ctrlEv = new EpollInterop.epoll_event
        {
            events = EpollInterop.EPOLLIN | EpollInterop.EPOLLET,
            data_fd = ctrlFd
        };
        EpollInterop.epoll_ctl(epfd, EpollInterop.EPOLL_CTL_ADD, ctrlFd, &ctrlEv);

        const int maxEvents = 64;
        var events = stackalloc EpollInterop.epoll_event[maxEvents];

        while (!_cts.IsCancellationRequested)
        {
            int n = EpollInterop.epoll_wait(epfd, events, maxEvents, 1000);
            for (int i = 0; i < n; i++)
            {
                int fd = events[i].data_fd;
                int ev = events[i].events;

                if (fd == ctrlFd)
                {
                    if ((ev & (EpollInterop.EPOLLERR | EpollInterop.EPOLLHUP)) != 0)
                        goto cleanup;
                    DrainCtrlFds(epfd, ctrlFd);
                    continue;
                }

                if ((ev & (EpollInterop.EPOLLERR | EpollInterop.EPOLLHUP)) != 0)
                {
                    CloseConn(epfd, fd);
                    continue;
                }

                HandleClientRead(epfd, fd);
            }
        }

        cleanup:
        for (int i = 0; i < MaxFd; i++)
        {
            if (_connBuf[i] != null)
            {
                EpollInterop.close(i);
                _bufPool.Push(_connBuf[i]);
                _connBuf[i] = null!;
            }
        }
        EpollInterop.close(epfd);
    }

    private unsafe void DrainCtrlFds(int epfd, int ctrlFd)
    {
        while (true)
        {
            int clientFd = FdPassingInterop.ReceiveFd(ctrlFd);
            if (clientFd < 0) break;
            if (clientFd >= MaxFd) { EpollInterop.close(clientFd); continue; }

            EpollInterop.SetNonBlocking(clientFd);
            EpollInterop.SetTcpNoDelay(clientFd);

            if (_connBuf[clientFd] != null)
                _bufPool.Push(_connBuf[clientFd]);
            _connBuf[clientFd] = _bufPool.Count > 0 ? _bufPool.Pop() : new byte[ConnBufSize];
            _connFilled[clientFd] = 0;

            var ev = new EpollInterop.epoll_event
            {
                events = EpollInterop.EPOLLIN | EpollInterop.EPOLLET | EpollInterop.EPOLLRDHUP,
                data_fd = clientFd
            };
            EpollInterop.epoll_ctl(epfd, EpollInterop.EPOLL_CTL_ADD, clientFd, &ev);
        }
    }

    private unsafe void HandleClientRead(int epfd, int fd)
    {
        byte[] buf = _connBuf[fd]!;
        fixed (byte* p = buf)
        {
            while (true)
            {
                int space = ConnBufSize - _connFilled[fd];
                if (space <= 0) { CloseConn(epfd, fd); return; }

                IntPtr r = EpollInterop.read(fd, p + _connFilled[fd], (IntPtr)space);
                int bytes = (int)r;

                if (bytes < 0)
                {
                    if (Marshal.GetLastPInvokeError() == EpollInterop.EAGAIN) return;
                    CloseConn(epfd, fd); return;
                }
                if (bytes == 0) { CloseConn(epfd, fd); return; }

                _connFilled[fd] += bytes;

                while (_connFilled[fd] > 0)
                {
                    int parsed = TryProcessOneFd(fd, new ReadOnlySpan<byte>(p, _connFilled[fd]));
                    if (parsed < 0) { CloseConn(epfd, fd); return; }
                    if (parsed == 0) break;
                    int remaining = _connFilled[fd] - parsed;
                    if (remaining > 0)
                        Buffer.BlockCopy(buf, parsed, buf, 0, remaining);
                    _connFilled[fd] = remaining;
                }
            }
        }
    }

    private int TryProcessOneFd(int fd, ReadOnlySpan<byte> data)
    {
        int headersEnd = data.IndexOf("\r\n\r\n"u8);
        if (headersEnd < 0) return 0;
        int bodyStart = headersEnd + 4;

        int rlNl = data.IndexOf((byte)'\n');
        if (rlNl < 1 || data[rlNl - 1] != (byte)'\r') return -1;
        var requestLine = data[..(rlNl - 1)];

        int sp1 = requestLine.IndexOf((byte)' ');
        if (sp1 < 0) return -1;
        var afterMethod = requestLine[(sp1 + 1)..];
        int sp2 = afterMethod.IndexOf((byte)' ');
        if (sp2 < 0) return -1;
        var method = requestLine[..sp1];
        var path = afterMethod[..sp2];

        if (method.SequenceEqual("GET"u8))
        {
            if (path.SequenceEqual("/ready"u8))
                SendAllFd(fd, _responses.Ready);
            else
                SendAllFd(fd, _responses.NotFound);
            return bodyStart;
        }

        if (!method.SequenceEqual("POST"u8) || !path.SequenceEqual("/fraud-score"u8))
        {
            SendAllFd(fd, _responses.NotFound);
            return bodyStart;
        }

        int cl = ParseContentLength(data[..headersEnd]);
        if (cl < 0 || cl > 4096)
        {
            SendAllFd(fd, _responses.BadRequest);
            return -1;
        }
        if (data.Length < bodyStart + cl) return 0;

        var body = data.Slice(bodyStart, cl);
        Span<float> vec = stackalloc float[16];
        vec.Clear();
        try
        {
            TransactionParser.Parse(body, vec);
            var (approved, fraudCount) = _detector.Score(vec);
            SendAllFd(fd, _responses.Get(approved, fraudCount));
        }
        catch
        {
            SendAllFd(fd, _responses.Get(true, 0));
        }
        return bodyStart + cl;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void SendAllFd(int fd, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                IntPtr n = EpollInterop.write(fd, p + sent, (IntPtr)(data.Length - sent));
                int written = (int)n;
                if (written > 0) { sent += written; continue; }
                if (written < 0 && Marshal.GetLastPInvokeError() == EpollInterop.EAGAIN) continue;
                return;
            }
        }
    }

    private unsafe void CloseConn(int epfd, int fd)
    {
        EpollInterop.epoll_ctl(epfd, EpollInterop.EPOLL_CTL_DEL, fd, null);
        EpollInterop.close(fd);
        if (_connBuf[fd] != null)
        {
            _bufPool.Push(_connBuf[fd]);
            _connBuf[fd] = null!;
        }
    }

    private void HandleConnectionSync(object? state)
    {
        var client = (Socket)state!;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        int filled = 0;
        try
        {
            while (true)
            {
                int read = client.Receive(buffer, filled, buffer.Length - filled, SocketFlags.None);
                if (read == 0) return;
                filled += read;

                while (filled > 0)
                {
                    int parsed = TryProcessOne(client, buffer.AsSpan(0, filled));
                    if (parsed < 0) return;
                    if (parsed == 0) break;
                    int remaining = filled - parsed;
                    if (remaining > 0)
                        Buffer.BlockCopy(buffer, parsed, buffer, 0, remaining);
                    filled = remaining;
                }

                if (filled == buffer.Length) return;
            }
        }
        catch { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try { client.Shutdown(SocketShutdown.Both); } catch { }
            client.Dispose();
        }
    }

    private int TryProcessOne(Socket client, ReadOnlySpan<byte> data)
    {
        int headersEnd = data.IndexOf("\r\n\r\n"u8);
        if (headersEnd < 0) return 0;
        int bodyStart = headersEnd + 4;

        int rlNl = data.IndexOf((byte)'\n');
        if (rlNl < 1 || data[rlNl - 1] != (byte)'\r') return -1;
        var requestLine = data[..(rlNl - 1)];

        int sp1 = requestLine.IndexOf((byte)' ');
        if (sp1 < 0) return -1;
        var afterMethod = requestLine[(sp1 + 1)..];
        int sp2 = afterMethod.IndexOf((byte)' ');
        if (sp2 < 0) return -1;
        var method = requestLine[..sp1];
        var path = afterMethod[..sp2];

        if (method.SequenceEqual("GET"u8))
        {
            if (path.SequenceEqual("/ready"u8))
                SendAll(client, _responses.Ready);
            else
                SendAll(client, _responses.NotFound);
            return bodyStart;
        }

        if (!method.SequenceEqual("POST"u8) || !path.SequenceEqual("/fraud-score"u8))
        {
            SendAll(client, _responses.NotFound);
            return bodyStart;
        }

        int cl = ParseContentLength(data[..headersEnd]);
        if (cl < 0 || cl > 4096)
        {
            SendAll(client, _responses.BadRequest);
            return -1;
        }
        if (data.Length < bodyStart + cl) return 0;

        var body = data.Slice(bodyStart, cl);
        Span<float> vec = stackalloc float[16];
        vec.Clear();
        try
        {
            if (_serverTimingEnabled && _detector is IvfDetector ivfDet)
            {
                ProcessWithTiming(client, ivfDet, body);
            }
            else
            {
                TransactionParser.Parse(body, vec);
                var (approved, fraudCount) = _detector.Score(vec);
                SendAll(client, _responses.Get(approved, fraudCount));
            }
        }
        catch
        {
            SendAll(client, _responses.Get(true, 0));
        }
        return bodyStart + cl;
    }

    private unsafe void ProcessWithTiming(Socket client, IvfDetector ivf, ReadOnlySpan<byte> body)
    {
        Span<float> vec = stackalloc float[16];
        vec.Clear();

        long tStart = Stopwatch.GetTimestamp();
        TransactionParser.Parse(body, vec);
        long tParseEnd = Stopwatch.GetTimestamp();

        long* ticks = stackalloc long[IvfDetector.TimingsCount];
        int* counts = stackalloc int[IvfDetector.CountersCount];
        for (int i = 0; i < IvfDetector.CountersCount; i++) counts[i] = 0;
        var (approved, fraudCount) = ivf.ScoreWithTimings(vec, ticks, counts);
        long tTotal = Stopwatch.GetTimestamp() - tStart;

        byte[] preBuilt = _responses.Get(approved, fraudCount);
        int sep = preBuilt.AsSpan().IndexOf("\r\n\r\n"u8);
        ReadOnlySpan<byte> jsonBody = preBuilt.AsSpan(sep + 4);

        var sb = new StringBuilder(180);
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Length: ").Append(jsonBody.Length).Append("\r\n");
        sb.Append("Content-Type: application/json\r\n");
        sb.Append("Server-Timing: ");
        sb.Append("parse;dur=").Append(((tParseEnd - tStart) * TickToUs).ToString("F2"));
        sb.Append(", s1-cent;dur=").Append((ticks[0] * TickToUs).ToString("F2"));
        sb.Append(", s1-scan;dur=").Append((ticks[1] * TickToUs).ToString("F2"));
        sb.Append(", s1-bbox;dur=").Append((ticks[2] * TickToUs).ToString("F2"));
        sb.Append(", s2-rerank;dur=").Append((ticks[3] * TickToUs).ToString("F2"));
        sb.Append(", total;dur=").Append((tTotal * TickToUs).ToString("F2"));
        sb.Append(", c-tri;dur=").Append(counts[0]);
        sb.Append(", c-bbox;dur=").Append(counts[1]);
        sb.Append(", c-scan;dur=").Append(counts[2]);
        sb.Append("\r\n\r\n");

        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        SendAll(client, head);
        SendAll(client, jsonBody);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ParseContentLength(ReadOnlySpan<byte> headers)
    {
        int idx = headers.IndexOf("Content-Length:"u8);
        if (idx < 0) idx = headers.IndexOf("content-length:"u8);
        if (idx < 0) return -1;
        int p = idx + 15;
        while (p < headers.Length && headers[p] == (byte)' ') p++;
        int v = 0;
        while (p < headers.Length && (uint)(headers[p] - '0') <= 9)
        {
            v = v * 10 + (headers[p] - (byte)'0');
            if (v > 65536) return -1;
            p++;
        }
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SendAll(Socket s, ReadOnlySpan<byte> data)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int n = s.Send(data[sent..], SocketFlags.None);
            if (n <= 0) throw new IOException("Socket send returned 0");
            sent += n;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { }
        try { _ctrlListener?.Close(); } catch { }
        _cts.Dispose();
    }
}

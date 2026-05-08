using System.Buffers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Rinha.Api;

/// <summary>
/// Minimal HTTP/1.1 server bypassing Kestrel: accepts on a Unix socket, parses
/// just enough of each request to dispatch (Method + Path + Content-Length),
/// and writes a pre-built complete-response buffer in a single Send.
///
/// Why bypass Kestrel: at 1 CPU + 350 MB the API's bottleneck is CFS quota,
/// and the doc-measured baseline shows ~7 ms of HTTP overhead per request
/// vs ~1 ms of actual IVF work. Kestrel's middleware pipeline, HttpContext
/// allocations, and header dictionaries dominate that overhead.
/// </summary>
public sealed class SocketHttpServer : IDisposable
{
    private readonly Socket _listener;
    private readonly IFraudDetector _detector;
    private readonly HttpResponseTable _responses;
    private readonly CancellationTokenSource _cts = new();

    public SocketHttpServer(string socketPath, IFraudDetector detector, HttpResponseTable responses, int backlog = 2048)
    {
        _detector = detector;
        _responses = responses;
        if (File.Exists(socketPath)) File.Delete(socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(backlog);
        File.SetUnixFileMode(socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
    }

    public async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }

            // ThreadPool.UnsafeQueueUserWorkItem em vez de Task.Run: evita
            // alocação de Task object + state machine por conexão. Sob 100
            // VUs com keepalive (k6 oficial) isso elimina dezenas de
            // alocações/seg que pressionavam o GC e geravam contention de
            // ThreadPool. Visto no daniloitagyba/rinha-2026-dotnet
            // (SERVER_MODE=raw → UnsafeQueueUserWorkItem).
            ThreadPool.UnsafeQueueUserWorkItem(
                static s => _ = s.server.HandleConnectionAsync(s.client),
                (server: this, client),
                preferLocal: false);
        }
    }

    private async Task HandleConnectionAsync(Socket client)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        int filled = 0;
        try
        {
            while (true)
            {
                int read = await client.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, _cts.Token);
                if (read == 0) return; // client closed
                filled += read;

                // Drain all complete pipelined requests already in the buffer
                // before going back to the kernel for more bytes.
                while (filled > 0)
                {
                    int parsed = TryProcessOne(client, buffer.AsSpan(0, filled));
                    if (parsed < 0) return;        // protocol error / explicit close
                    if (parsed == 0) break;        // need more bytes
                    int remaining = filled - parsed;
                    if (remaining > 0)
                        Buffer.BlockCopy(buffer, parsed, buffer, 0, remaining);
                    filled = remaining;
                }

                // Buffer full and no progress made → request larger than 8 KB. Drop.
                if (filled == buffer.Length) return;
            }
        }
        catch { /* connection-level errors are non-fatal */ }
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
            TransactionParser.Parse(body, vec);
            var (approved, fraudCount) = _detector.Score(vec);
            SendAll(client, _responses.Get(approved, fraudCount));
        }
        catch
        {
            // FN (weight 3) cheaper than 5xx (weight 5). Always 200.
            SendAll(client, _responses.Get(true, 0));
        }
        return bodyStart + cl;
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
        _cts.Dispose();
    }
}

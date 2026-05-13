using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rinha.Api;

internal static unsafe class EpollInterop
{
    [DllImport("libc", SetLastError = true)]
    public static extern int epoll_create1(int flags);

    [DllImport("libc", SetLastError = true)]
    public static extern int epoll_ctl(int epfd, int op, int fd, epoll_event* ev);

    [DllImport("libc", SetLastError = true)]
    public static extern int epoll_wait(int epfd, epoll_event* events, int maxevents, int timeout);

    [DllImport("libc", SetLastError = true)]
    public static extern IntPtr read(int fd, byte* buf, IntPtr count);

    [DllImport("libc", SetLastError = true)]
    public static extern IntPtr write(int fd, byte* buf, IntPtr count);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    public static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    public static extern int setsockopt(int sockfd, int level, int optname, void* optval, int optlen);

    public const int EPOLL_CTL_ADD = 1;
    public const int EPOLL_CTL_DEL = 2;

    public const int EPOLLIN = 0x001;
    public const int EPOLLET = unchecked((int)0x80000000u);
    public const int EPOLLRDHUP = 0x2000;
    public const int EPOLLHUP = 0x010;
    public const int EPOLLERR = 0x008;

    public const int F_GETFL = 3;
    public const int F_SETFL = 4;
    public const int O_NONBLOCK = 0x800;

    public const int IPPROTO_TCP = 6;
    public const int TCP_NODELAY = 1;
    public const int EAGAIN = 11;

    [StructLayout(LayoutKind.Explicit, Size = 12, Pack = 1)]
    public struct epoll_event
    {
        [FieldOffset(0)] public int events;
        [FieldOffset(4)] public int data_fd;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetNonBlocking(int fd)
    {
        int flags = fcntl(fd, F_GETFL, 0);
        fcntl(fd, F_SETFL, flags | O_NONBLOCK);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetTcpNoDelay(int fd)
    {
        int val = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &val, sizeof(int));
    }
}

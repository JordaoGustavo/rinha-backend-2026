using System.Runtime.InteropServices;

namespace Rinha.Api;

internal static unsafe class FdPassingInterop
{
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    private struct Msghdr
    {
        [FieldOffset(0)]  public IntPtr msg_name;
        [FieldOffset(8)]  public int msg_namelen;
        [FieldOffset(16)] public IntPtr msg_iov;
        [FieldOffset(24)] public IntPtr msg_iovlen;
        [FieldOffset(32)] public IntPtr msg_control;
        [FieldOffset(40)] public IntPtr msg_controllen;
        [FieldOffset(48)] public int msg_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec
    {
        public IntPtr iov_base;
        public IntPtr iov_len;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr recvmsg(int sockfd, ref Msghdr msg, int flags);

    private const int SOL_SOCKET = 1;
    private const int SCM_RIGHTS = 1;
    // CMSG_SPACE(sizeof(int)) on x86_64 = 24
    private const int ControlBufSize = 24;

    public static int ReceiveFd(int sockFd)
    {
        byte dataBuf = 0;
        Iovec iov;
        iov.iov_base = (IntPtr)(&dataBuf);
        iov.iov_len = (IntPtr)1;

        byte* controlBuf = stackalloc byte[ControlBufSize];
        new Span<byte>(controlBuf, ControlBufSize).Clear();

        Msghdr msg = default;
        msg.msg_iov = (IntPtr)(&iov);
        msg.msg_iovlen = (IntPtr)1;
        msg.msg_control = (IntPtr)controlBuf;
        msg.msg_controllen = (IntPtr)ControlBufSize;

        IntPtr ret = recvmsg(sockFd, ref msg, 0);
        if ((long)ret <= 0) return -1;

        if ((long)msg.msg_controllen < 20) return -1;

        int cmsgLevel = *(int*)(controlBuf + 8);
        int cmsgType = *(int*)(controlBuf + 12);
        if (cmsgLevel != SOL_SOCKET || cmsgType != SCM_RIGHTS) return -1;

        return *(int*)(controlBuf + 16);
    }
}

using System.Runtime.InteropServices;

namespace Rinha.Api;

/// <summary>
/// Best-effort Linux mmap advice hints. All calls are no-ops on non-Linux
/// hosts and ignore syscall failures (madvise can fail under containerized
/// LSM policies; the index still works without the hints).
/// </summary>
internal static class MmapHints
{
    // Linux <sys/mman.h>:
    private const int MADV_NORMAL     = 0;
    private const int MADV_RANDOM     = 1;
    private const int MADV_SEQUENTIAL = 2;
    private const int MADV_WILLNEED   = 3;
    private const int MADV_DONTNEED   = 4;
    private const int MADV_HUGEPAGE   = 14;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(IntPtr addr, nuint length, int advice);

    /// <summary>
    /// Hint kernel to back the range with 2 MB transparent huge pages where
    /// possible. Mac mini Haswell L1 dTLB has ~64 entries; with 4 KB pages
    /// our 110 MB IVF index spans ~28 k pages → constant TLB churn. With
    /// 2 MB huge pages the same range fits in ~55 entries (entire L1 TLB).
    /// </summary>
    public static unsafe void HintHugePages(void* addr, long length)
    {
        if (!OperatingSystem.IsLinux()) return;
        _ = Madvise((IntPtr)addr, (nuint)length, MADV_HUGEPAGE);
    }

    /// <summary>
    /// Hint kernel that the entire range will be touched soon — kicks off
    /// readahead so the first real query doesn't pay page-fault cost.
    /// </summary>
    public static unsafe void HintWillNeed(void* addr, long length)
    {
        if (!OperatingSystem.IsLinux()) return;
        _ = Madvise((IntPtr)addr, (nuint)length, MADV_WILLNEED);
    }

    /// <summary>
    /// Hint kernel that access will be random (e.g., the float32 rerank
    /// reference). Disables sequential readahead which would waste bandwidth
    /// on a 168 MB file we touch ~6 vectors at a time.
    /// </summary>
    public static unsafe void HintRandom(void* addr, long length)
    {
        if (!OperatingSystem.IsLinux()) return;
        _ = Madvise((IntPtr)addr, (nuint)length, MADV_RANDOM);
    }
}

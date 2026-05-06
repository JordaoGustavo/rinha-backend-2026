using System.Runtime.InteropServices;

namespace Rinha.Api;

public static class IvfBinaryFormat
{
    public static ReadOnlySpan<byte> Magic => "IVFR"u8;
    public const uint Version = 7;
    public const int HeaderSize = 64;
    public const int Dims = 14;
    public const int PaddedDims = 16;
    public const int Scale = 4096;
    public const int BlockVectors = 8;
    public const int BlockBytes = BlockVectors * PaddedDims * sizeof(short);
    public const int CentroidVectorBytes = PaddedDims * sizeof(short);
    public const int Int16VectorBytes = PaddedDims * sizeof(short);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ClusterMeta
    {
        public uint Offset;
        public uint Count;
    }

    public static long CentroidsOffset => HeaderSize;

    public static long BboxMinOffset(int numClusters) =>
        CentroidsOffset + (long)numClusters * CentroidVectorBytes;

    public static long BboxMaxOffset(int numClusters) =>
        BboxMinOffset(numClusters) + (long)numClusters * Int16VectorBytes;

    public static long ClusterMetaOffset(int numClusters) =>
        BboxMaxOffset(numClusters) + (long)numClusters * Int16VectorBytes;

    public static long VectorsOffset(int numClusters) =>
        ClusterMetaOffset(numClusters) + (long)numClusters * 8;

    public static long LabelsOffset(int numClusters, int totalVectorSlots) =>
        VectorsOffset(numClusters) + (long)totalVectorSlots * Int16VectorBytes;

    // Original global indices: int32 per slot, stored after labels
    public static long OriginalIndicesOffset(int numClusters, int totalVectorSlots) =>
        LabelsOffset(numClusters, totalVectorSlots) + totalVectorSlots;

    public static long TotalSize(int numClusters, int totalVectorSlots) =>
        OriginalIndicesOffset(numClusters, totalVectorSlots) + (long)totalVectorSlots * sizeof(int);
}

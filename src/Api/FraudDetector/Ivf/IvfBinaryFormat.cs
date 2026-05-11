using System.Runtime.InteropServices;

namespace Rinha.Api;

public static class IvfBinaryFormat
{
    public static ReadOnlySpan<byte> Magic => "IVFR"u8;
    public const uint Version = 10;
    public const int HeaderSize = 64;
    public const int Dims = 14;
    public const int PaddedDims = 16;
    public const int Scale = 4096;
    public const int Int8Scale = 128;
    public const int Int16ToInt8Shift = 5; // 4096 / 128 = 32 = 2^5
    public const int BlockVectors = 8;
    public const int BlockBytes = BlockVectors * PaddedDims * sizeof(sbyte);
    public const int CentroidVectorBytes = PaddedDims * sizeof(short);
    public const int Int16VectorBytes = PaddedDims * sizeof(short);
    public const int Int8VectorBytes = PaddedDims * sizeof(sbyte);
    // v8: per-cluster radius (uint, ceil(sqrt(max int16-squared centroid→vec
    // distance))). Used by pass-2 triangle-inequality skip before bbox-LB.
    public const int ClusterRadiusBytes = sizeof(uint);

    // v9: profile fast path data. 22-bit hash over a
    // subset of dims (amt-ratio, kmHome, txCount, mccRisk, amt, no-last,
    // online, cardPresent, unknownMerchant, kmCurrent, instThreshold,
    // merchAvgAmt). Bucket monochromático com count >= threshold devolve
    // resposta sem rodar IVF.
    public const int ProfileBits = 22;
    public const int ProfileKeyCount = 1 << ProfileBits;     // 4 194 304
    public const long ProfileMaskBytes = ProfileKeyCount * sizeof(byte);    // 4 MB
    public const long ProfileCountBytes = ProfileKeyCount * sizeof(ushort); // 8 MB
    public const byte ProfileLegitMask = 1;
    public const byte ProfileFraudMask = 2;

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

    public static long ClusterRadiusOffset(int numClusters) =>
        BboxMaxOffset(numClusters) + (long)numClusters * Int16VectorBytes;

    public static long ClusterMetaOffset(int numClusters) =>
        ClusterRadiusOffset(numClusters) + (long)numClusters * ClusterRadiusBytes;

    public static long VectorsOffset(int numClusters) =>
        ClusterMetaOffset(numClusters) + (long)numClusters * 8;

    public static long LabelsOffset(int numClusters, int totalVectorSlots) =>
        VectorsOffset(numClusters) + (long)totalVectorSlots * Int8VectorBytes;

    // Original global indices: int32 per slot, stored after labels
    public static long OriginalIndicesOffset(int numClusters, int totalVectorSlots) =>
        LabelsOffset(numClusters, totalVectorSlots) + totalVectorSlots;

    // v9: profile fast path arrays appended after originalIndices. Mask is
    // byte-per-key (4 MB), count is ushort-per-key (8 MB).
    public static long ProfileMaskOffset(int numClusters, int totalVectorSlots) =>
        OriginalIndicesOffset(numClusters, totalVectorSlots) + (long)totalVectorSlots * sizeof(int);

    public static long ProfileCountOffset(int numClusters, int totalVectorSlots) =>
        ProfileMaskOffset(numClusters, totalVectorSlots) + ProfileMaskBytes;

    public static long TotalSize(int numClusters, int totalVectorSlots) =>
        ProfileCountOffset(numClusters, totalVectorSlots) + ProfileCountBytes;
}

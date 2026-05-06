using System.Runtime.InteropServices;

namespace Rinha.Api;

public static class IvfBinaryFormat
{
    public static ReadOnlySpan<byte> Magic => "IVFR"u8;
    public const uint Version = 5;
    public const int HeaderSize = 64;
    public const int Dims = 14;
    public const int PaddedDims = 16;
    public const int Scale = 4096;

    /// <summary>Number of vectors packed per block for SIMD batched distance.</summary>
    public const int BlockVectors = 8;

    /// <summary>
    /// Bytes per block: 8 vectors × 16 padded dims × 2 bytes (int16) = 256 bytes (4 cache lines).
    /// Internal layout is dim-pair-interleaved across the 8 vectors so that one AVX2
    /// MultiplyAddAdjacent computes the squared diffs of one dim-pair across all 8 vectors
    /// in a single instruction (output = 8 int32 partial distances, one per vector).
    ///
    /// Layout per block (k = 0..7, each row = 32 bytes = 1 AVX2 register):
    ///   [d_{2k}_v0, d_{2k+1}_v0, d_{2k}_v1, d_{2k+1}_v1, ..., d_{2k}_v7, d_{2k+1}_v7]
    ///
    /// To match this layout, the broadcasted query for dim-pair k is built as
    /// `Vector256.Create(int32)` where the int32 packs `q[2k]` (low 16 bits) and
    /// `q[2k+1]` (high 16 bits) — broadcast replicates that pattern 8 times.
    /// </summary>
    public const int BlockBytes = BlockVectors * PaddedDims * sizeof(short);

    public const int CentroidVectorBytes = PaddedDims * sizeof(float);
    public const int Int16VectorBytes = PaddedDims * sizeof(short);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ClusterMeta
    {
        /// <summary>Vector index of the first vector in this cluster. Must be a multiple of BlockVectors.</summary>
        public uint Offset;
        /// <summary>Number of valid vectors in this cluster (last block may be partially valid).</summary>
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

    /// <summary>
    /// totalVectorSlots is paddedNumVectors (number of vectors after padding each cluster
    /// to a multiple of BlockVectors). Stored vectors take totalVectorSlots × Int16VectorBytes.
    /// Labels region has the same indexing (totalVectorSlots bytes).
    /// </summary>
    public static long LabelsOffset(int numClusters, int totalVectorSlots) =>
        VectorsOffset(numClusters) + (long)totalVectorSlots * Int16VectorBytes;

    public static long TotalSize(int numClusters, int totalVectorSlots) =>
        LabelsOffset(numClusters, totalVectorSlots) + totalVectorSlots;
}

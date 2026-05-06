using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace Rinha.Api;

public sealed unsafe class KmknnIndex : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePtr;

    private readonly short* _centroids;
    private readonly short* _bboxMin;
    private readonly short* _bboxMax;
    private readonly KmknnBinaryFormat.ClusterMeta* _clusterMeta;
    private readonly short* _vectors;
    private readonly byte* _labels;

    public int NumVectors { get; }
    public int NumClusters { get; }

    private KmknnIndex(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, byte* basePtr,
        int numVectors, int numClusters)
    {
        _mmf = mmf;
        _accessor = accessor;
        _basePtr = basePtr;
        NumVectors = numVectors;
        NumClusters = numClusters;

        _centroids = (short*)(_basePtr + KmknnBinaryFormat.CentroidsOffset);
        _bboxMin = (short*)(_basePtr + KmknnBinaryFormat.BboxMinOffset(numClusters));
        _bboxMax = (short*)(_basePtr + KmknnBinaryFormat.BboxMaxOffset(numClusters));
        _clusterMeta = (KmknnBinaryFormat.ClusterMeta*)(_basePtr + KmknnBinaryFormat.ClusterMetaOffset(numClusters));
        _vectors = (short*)(_basePtr + KmknnBinaryFormat.VectorsOffset(numClusters));
        _labels = _basePtr + KmknnBinaryFormat.LabelsOffset(numClusters, numVectors);
    }

    public static KmknnIndex Open(string path)
    {
        var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

        uint magic = *(uint*)ptr;
        uint expectedMagic = ((uint)'K') | ((uint)'M' << 8) | ((uint)'K' << 16) | ((uint)'N' << 24);
        if (magic != expectedMagic)
            throw new InvalidDataException("Invalid index magic — expected KMKN");

        uint version = *(uint*)(ptr + 4);
        if (version != KmknnBinaryFormat.Version)
            throw new InvalidDataException($"Unsupported KMKN version: {version}");

        int numVectors  = (int)*(uint*)(ptr + 8);
        int numClusters = (int)*(uint*)(ptr + 12);

        return new KmknnIndex(mmf, accessor, ptr, numVectors, numClusters);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short* GetCentroidPtr(int clusterIndex) =>
        _centroids + clusterIndex * KmknnBinaryFormat.PaddedDims;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short* GetBboxMinPtr(int clusterIndex) =>
        _bboxMin + clusterIndex * KmknnBinaryFormat.PaddedDims;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short* GetBboxMaxPtr(int clusterIndex) =>
        _bboxMax + clusterIndex * KmknnBinaryFormat.PaddedDims;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref KmknnBinaryFormat.ClusterMeta GetClusterMeta(int clusterIndex) =>
        ref _clusterMeta[clusterIndex];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short* GetVectorPtr(int vectorIndex) =>
        _vectors + vectorIndex * KmknnBinaryFormat.PaddedDims;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetLabel(int vectorIndex) => _labels[vectorIndex];

    public void Prefault()
    {
        long totalSize = KmknnBinaryFormat.TotalSize(NumClusters, NumVectors);
        for (long i = 0; i < totalSize; i += 4096)
            _ = _basePtr[i];
    }

    public void Dispose()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
    }
}

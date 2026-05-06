using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace Rinha.Api;

public sealed unsafe class ExactDetector : IFraudDetector
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePtr;

    private readonly float* _vectors;
    private readonly byte* _labels;

    public int NumVectors { get; }
    public int NumClusters => 1;
    public string Description => $"Exact float32 brute-force, {NumVectors} vectors";

    private ExactDetector(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, byte* basePtr, int numVectors)
    {
        _mmf = mmf;
        _accessor = accessor;
        _basePtr = basePtr;
        NumVectors = numVectors;

        _vectors = (float*)(_basePtr + ExactBinaryFormat.VectorsOffset);
        _labels  = _basePtr + ExactBinaryFormat.LabelsOffset(numVectors);
    }

    public static ExactDetector Open(string path)
    {
        var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

        uint version = *(uint*)(ptr + 4);
        if (version != ExactBinaryFormat.Version)
            throw new InvalidDataException($"Unsupported EXCT version: {version}");

        int numVectors = (int)*(uint*)(ptr + 8);
        return new ExactDetector(mmf, accessor, ptr, numVectors);
    }

    public void Prefault()
    {
        long total = ExactBinaryFormat.TotalSize(NumVectors);
        for (long i = 0; i < total; i += 4096)
            _ = _basePtr[i];
    }

    public (bool Approved, int FraudCount) Score(ReadOnlySpan<float> query)
    {
        const int pd = ExactBinaryFormat.PaddedDims;
        Span<int>   topIdx = stackalloc int[5];
        Span<float> topDist = stackalloc float[5];
        int size = 0;

        fixed (float* q = query)
        {
            for (int i = 0; i < NumVectors; i++)
            {
                float dist = SimdDistance.EuclideanSquaredPtr(q, _vectors + i * pd);
                if (size < 5)
                {
                    HeapPush(topIdx, topDist, ref size, i, dist);
                }
                else if (dist < topDist[0])
                {
                    topIdx[0] = i;
                    topDist[0] = dist;
                    SiftDown(topIdx, topDist, 5, 0);
                }
            }
        }

        int fraudCount = 0;
        for (int i = 0; i < size; i++)
            fraudCount += _labels[topIdx[i]];

        return (fraudCount < 3, fraudCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HeapPush(Span<int> idx, Span<float> dist, ref int size, int v, float d)
    {
        int i = size++;
        idx[i] = v; dist[i] = d;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (dist[parent] >= dist[i]) break;
            (idx[parent], idx[i]) = (idx[i], idx[parent]);
            (dist[parent], dist[i]) = (dist[i], dist[parent]);
            i = parent;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SiftDown(Span<int> idx, Span<float> dist, int size, int i)
    {
        while (true)
        {
            int largest = i, l = 2 * i + 1, r = 2 * i + 2;
            if (l < size && dist[l] > dist[largest]) largest = l;
            if (r < size && dist[r] > dist[largest]) largest = r;
            if (largest == i) break;
            (idx[largest], idx[i]) = (idx[i], idx[largest]);
            (dist[largest], dist[i]) = (dist[i], dist[largest]);
            i = largest;
        }
    }

    public void Dispose()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
    }
}

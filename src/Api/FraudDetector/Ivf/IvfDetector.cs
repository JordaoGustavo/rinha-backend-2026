using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Rinha.Api;

public sealed unsafe class IvfDetector : IFraudDetector
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePtr;

    // Optional exact float32 reranking data (from exact.bin mmap)
    private readonly MemoryMappedFile? _exactMmf;
    private readonly MemoryMappedViewAccessor? _exactAccessor;
    private readonly float* _exactVectors; // null if not loaded

    private readonly float* _centroids;
    private readonly short* _bboxMin;
    private readonly short* _bboxMax;
    private readonly IvfBinaryFormat.ClusterMeta* _clusterMeta;
    private readonly short* _vectors;
    private readonly byte* _labels;
    private readonly int* _originalIndices; // slot → original global index in exact.bin

    public int NumVectors { get; }
    public int NumClusters { get; }
    public int TotalSlots { get; }
    public int NprobeFast { get; }
    public int NprobeFull { get; }
    public string Description => $"IVF v6 SoA-block {NumClusters} clusters, int16 + f32-rerank, nprobe={NprobeFull}";

    private IvfDetector(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, byte* basePtr,
        int numVectors, int numClusters, int totalSlots, int nprobeFast, int nprobeFull,
        MemoryMappedFile? exactMmf = null, MemoryMappedViewAccessor? exactAccessor = null, float* exactVectors = null)
    {
        _mmf = mmf;
        _accessor = accessor;
        _basePtr = basePtr;
        _exactMmf = exactMmf;
        _exactAccessor = exactAccessor;
        _exactVectors = exactVectors;
        NumVectors = numVectors;
        NumClusters = numClusters;
        TotalSlots = totalSlots;

        int envFast = int.TryParse(Environment.GetEnvironmentVariable("NPROBE_FAST"), out var nf) ? nf : 0;
        int envFull = int.TryParse(Environment.GetEnvironmentVariable("NPROBE_FULL"), out var nF) ? nF : 0;
        NprobeFast = envFast > 0 ? envFast : nprobeFast;
        NprobeFull = envFull > 0 ? envFull : nprobeFull;

        _centroids = (float*)(_basePtr + IvfBinaryFormat.CentroidsOffset);
        _bboxMin = (short*)(_basePtr + IvfBinaryFormat.BboxMinOffset(numClusters));
        _bboxMax = (short*)(_basePtr + IvfBinaryFormat.BboxMaxOffset(numClusters));
        _clusterMeta = (IvfBinaryFormat.ClusterMeta*)(_basePtr + IvfBinaryFormat.ClusterMetaOffset(numClusters));
        _vectors = (short*)(_basePtr + IvfBinaryFormat.VectorsOffset(numClusters));
        _labels = _basePtr + IvfBinaryFormat.LabelsOffset(numClusters, totalSlots);
        _originalIndices = (int*)(_basePtr + IvfBinaryFormat.OriginalIndicesOffset(numClusters, totalSlots));
    }

    public static IvfDetector Open(string path)
    {
        var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

        uint version = *(uint*)(ptr + 4);
        if (version != IvfBinaryFormat.Version)
            throw new InvalidDataException($"Unsupported IVFR version: {version} (expected {IvfBinaryFormat.Version}). Re-run preprocess.");

        int numVectors  = (int)*(uint*)(ptr + 8);
        int numClusters = (int)*(uint*)(ptr + 12);
        int nprobeFast  = (int)*(uint*)(ptr + 24);
        int nprobeFull  = (int)*(uint*)(ptr + 28);
        int totalSlots  = (int)*(uint*)(ptr + 36);

        return new IvfDetector(mmf, accessor, ptr, numVectors, numClusters, totalSlots, nprobeFast, nprobeFull);
    }

    /// <summary>
    /// Opens the IVF index with an additional exact float32 reference for true-float32 reranking.
    /// Achieves 100% agreement with the ExactDetector oracle.
    /// </summary>
    public static IvfDetector OpenWithExactRerank(string ivfPath, string exactPath)
    {
        var mmf = MemoryMappedFile.CreateFromFile(ivfPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

        uint version = *(uint*)(ptr + 4);
        if (version != IvfBinaryFormat.Version)
            throw new InvalidDataException($"Unsupported IVFR version: {version} (expected {IvfBinaryFormat.Version}). Re-run preprocess.");

        int numVectors  = (int)*(uint*)(ptr + 8);
        int numClusters = (int)*(uint*)(ptr + 12);
        int nprobeFast  = (int)*(uint*)(ptr + 24);
        int nprobeFull  = (int)*(uint*)(ptr + 28);
        int totalSlots  = (int)*(uint*)(ptr + 36);

        var exactMmf = MemoryMappedFile.CreateFromFile(exactPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var exactAccessor = exactMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* exactPtr = null;
        exactAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref exactPtr);
        float* exactVectors = (float*)(exactPtr + ExactBinaryFormat.VectorsOffset);

        return new IvfDetector(mmf, accessor, ptr, numVectors, numClusters, totalSlots, nprobeFast, nprobeFull,
                               exactMmf, exactAccessor, exactVectors);
    }

    public void Prefault()
    {
        long totalSize = IvfBinaryFormat.TotalSize(NumClusters, TotalSlots);
        for (long i = 0; i < totalSize; i += 4096)
            _ = _basePtr[i];

        if (_exactVectors != null)
        {
            // Walk the float32 reference data so the first real query doesn't
            // pay random page-fault cost during the rerank stage.
            const int pd = IvfBinaryFormat.PaddedDims;
            long exactBytes = (long)NumVectors * pd * sizeof(float);
            byte* exactBase = (byte*)_exactVectors;
            for (long i = 0; i < exactBytes; i += 4096)
                _ = exactBase[i];
        }
    }

    /// <summary>
    /// Per-stage timing layout for ScoreWithTimings: 4 longs = ticks for
    /// [centroidScan, initialClusterScan, bboxRepairPass, float32Rerank].
    /// </summary>
    public const int TimingsCount = 4;

    public (bool Approved, int FraudCount) Score(ReadOnlySpan<float> query)
        => ScoreCore(query, ticksOut: null);

    /// <summary>
    /// Dev-only path. Same algorithm as <see cref="Score"/>, but writes
    /// per-stage tick counts to <paramref name="ticksOut"/> (length 4).
    /// </summary>
    public (bool Approved, int FraudCount) ScoreWithTimings(ReadOnlySpan<float> query, long* ticksOut)
        => ScoreCore(query, ticksOut);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (bool Approved, int FraudCount) ScoreCore(ReadOnlySpan<float> query, long* ticksOut)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        const int scale = IvfBinaryFormat.Scale;
        const int rerankN = 32;

        short* qInt = stackalloc short[pd];
        for (int d = 0; d < pd; d++)
        {
            float v = MathF.Round(query[d] * scale);
            if (v > short.MaxValue) qInt[d] = short.MaxValue;
            else if (v < short.MinValue) qInt[d] = short.MinValue;
            else qInt[d] = (short)v;
        }

        int* qPairs = stackalloc int[pd / 2];
        for (int kp = 0; kp < pd / 2; kp++)
            qPairs[kp] = ((ushort)qInt[2 * kp]) | ((ushort)qInt[2 * kp + 1] << 16);

        // All hot-path buffers allocated ONCE, before any loop, so we don't
        // grow the stack frame across the rerankN rerank iterations.
        Span<int>   candidateIdx = stackalloc int[rerankN];
        float*      candVec      = stackalloc float[pd];
        Span<int>   topIdx       = stackalloc int[5];
        Span<float> topDist      = stackalloc float[5];

        int fraudCount;
        fixed (float* qFloat = query)
        {
            // Stage 1: provably-correct int16 search over all 4096 clusters.
            //   NprobeFull seeds the heap with the closest centroids;
            //   useBboxRepair=true then visits every other cluster whose
            //   bbox lower-bound ≤ current worst-K. Output is top-rerankN
            //   by int16 distance.
            int candidateCount = FindKNearest(qFloat, qInt, qPairs, rerankN, candidateIdx,
                                              NprobeFull, useBboxRepair: true, ticksOut);

            long s2Start = ticksOut != null ? Stopwatch.GetTimestamp() : 0;

            // Stage 2: float32 rerank using true float32 vectors (from exact.bin if available,
            // otherwise dequantized int16 — exact.bin gives 100% oracle agreement).
            float* exactVecs = _exactVectors;
            int* origIdx = _originalIndices;

            int topSize = 0;
            for (int i = 0; i < candidateCount; i++)
            {
                int vecIdx = candidateIdx[i];
                float dist;
                if (exactVecs != null)
                {
                    int origGlobalIdx = origIdx[vecIdx];
                    dist = SimdDistance.EuclideanSquaredPtr(qFloat, exactVecs + (long)origGlobalIdx * pd);
                }
                else
                {
                    UnpackFloat(vecIdx, candVec, pd, scale);
                    dist = SimdDistance.EuclideanSquaredPtr(qFloat, candVec);
                }

                if (topSize < 5)
                {
                    int p = topSize++;
                    topIdx[p] = vecIdx; topDist[p] = dist;
                    int j = p;
                    while (j > 0)
                    {
                        int parent = (j - 1) >> 1;
                        if (topDist[parent] >= topDist[j]) break;
                        (topIdx[parent], topIdx[j]) = (topIdx[j], topIdx[parent]);
                        (topDist[parent], topDist[j]) = (topDist[j], topDist[parent]);
                        j = parent;
                    }
                }
                else if (dist < topDist[0])
                {
                    topIdx[0] = vecIdx;
                    topDist[0] = dist;
                    SiftDownFloat(topIdx, topDist, 5, 0);
                }
            }

            fraudCount = 0;
            for (int i = 0; i < topSize; i++)
                fraudCount += _labels[topIdx[i]];

            if (ticksOut != null)
                ticksOut[3] = Stopwatch.GetTimestamp() - s2Start;
        }

        return (fraudCount < 3, fraudCount);
    }

    private int FindKNearest(float* qFloat, short* qInt, int* qPairs, int k, Span<int> resultIdx,
        int nprobe, bool useBboxRepair, long* ticksOut = null)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        int numClusters = NumClusters;
        int actualNprobe = Math.Min(nprobe, numClusters);

        int*   probeList  = stackalloc int[actualNprobe];
        float* probeDists = stackalloc float[actualNprobe];
        int probeCount = 0;

        long t0 = ticksOut != null ? Stopwatch.GetTimestamp() : 0;

        for (int c = 0; c < numClusters; c++)
        {
            float dist = SimdDistance.EuclideanSquaredPtr(qFloat, _centroids + c * pd);

            if (probeCount < actualNprobe)
            {
                int pos = probeCount++;
                probeList[pos] = c;
                probeDists[pos] = dist;
                int i = pos;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (probeDists[parent] >= probeDists[i]) break;
                    (probeList[parent], probeList[i]) = (probeList[i], probeList[parent]);
                    (probeDists[parent], probeDists[i]) = (probeDists[i], probeDists[parent]);
                    i = parent;
                }
            }
            else if (dist < probeDists[0])
            {
                probeList[0] = c;
                probeDists[0] = dist;
                SiftDownProbe(probeList, probeDists, probeCount, 0);
            }
        }

        int* heapIdx = stackalloc int[k];
        int* heapDist = stackalloc int[k];
        int heapSize = 0;

        long t1 = ticksOut != null ? Stopwatch.GetTimestamp() : 0;
        if (ticksOut != null) ticksOut[0] = t1 - t0;

        for (int p = 0; p < probeCount; p++)
            ScanCluster(qInt, qPairs, probeList[p], heapIdx, heapDist, ref heapSize, k);

        long t2 = ticksOut != null ? Stopwatch.GetTimestamp() : 0;
        if (ticksOut != null) ticksOut[1] = t2 - t1;

        if (useBboxRepair && probeCount < numClusters)
        {
            // Bitset of already-scanned clusters: O(1) test vs O(probeCount) linear walk.
            int bitsetWords = (numClusters + 63) >> 6;
            ulong* scannedBits = stackalloc ulong[bitsetWords];
            for (int w = 0; w < bitsetWords; w++) scannedBits[w] = 0UL;
            for (int j = 0; j < probeCount; j++)
            {
                int c = probeList[j];
                scannedBits[c >> 6] |= 1UL << (c & 63);
            }

            int worstDist = heapSize == k ? heapDist[0] : int.MaxValue;
            for (int c = 0; c < numClusters; c++)
            {
                if ((scannedBits[c >> 6] & (1UL << (c & 63))) != 0) continue;

                int lb = SimdDistance.Int16BboxLowerBound(qInt, _bboxMin + c * pd, _bboxMax + c * pd);
                if (lb <= worstDist)
                {
                    ScanCluster(qInt, qPairs, c, heapIdx, heapDist, ref heapSize, k);
                    if (heapSize == k) worstDist = heapDist[0];
                }
            }
        }

        if (ticksOut != null) ticksOut[2] = Stopwatch.GetTimestamp() - t2;

        int resultCount = heapSize;
        for (int i = heapSize - 1; i >= 0; i--)
        {
            resultIdx[i] = heapIdx[0];
            HeapPopKnn(heapIdx, heapDist, ref heapSize);
        }
        return resultCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ScanCluster(short* qInt, int* qPairs, int clusterId,
        int* heapIdx, int* heapDist, ref int heapSize, int k)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        const int bv = IvfBinaryFormat.BlockVectors;

        ref var meta = ref _clusterMeta[clusterId];
        int offset = (int)meta.Offset;
        int count = (int)meta.Count;
        if (count == 0) return;

        int numBlocks = (count + bv - 1) / bv;
        short* clusterVecBase = _vectors + offset * pd;

        if (Avx2.IsSupported)
        {
            ScanClusterAvx2(qPairs, clusterVecBase, offset, count, numBlocks, heapIdx, heapDist, ref heapSize, k);
        }
        else
        {
            ScanClusterScalar(qInt, clusterVecBase, offset, count, numBlocks, heapIdx, heapDist, ref heapSize, k);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScanClusterAvx2(int* qPairs, short* clusterVecBase, int offset, int count, int numBlocks,
        int* heapIdx, int* heapDist, ref int heapSize, int k)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        const int bv = IvfBinaryFormat.BlockVectors;
        const int blockShorts = pd * bv;
        const int dimPairs = pd / 2;
        const int earlyExitDimPairs = 4;

        Span<Vector256<short>> qBroadcast = stackalloc Vector256<short>[dimPairs];
        for (int kp = 0; kp < dimPairs; kp++)
            qBroadcast[kp] = Vector256.Create(qPairs[kp]).AsInt16();

        Span<int> dists = stackalloc int[bv];

        for (int b = 0; b < numBlocks; b++)
        {
            short* blockPtr = clusterVecBase + b * blockShorts;

            if (b + 2 < numBlocks)
                Sse.Prefetch0(blockPtr + 2 * blockShorts);

            int validLanes = count - b * bv;
            if (validLanes > bv) validLanes = bv;

            Vector256<int> partial = Vector256<int>.Zero;
            for (int kp = 0; kp < earlyExitDimPairs; kp++)
            {
                var v = Avx2.LoadVector256(blockPtr + kp * 16).AsInt16();
                var diff = Avx2.Subtract(v, qBroadcast[kp]);
                partial = Avx2.Add(partial, Avx2.MultiplyAddAdjacent(diff, diff));
            }

            int worst = (heapSize == k) ? heapDist[0] : int.MaxValue;
            var threshold = Vector256.Create(worst);
            var ltMask = Avx2.CompareGreaterThan(threshold, partial);
            int passMask = Avx2.MoveMask(ltMask.AsByte());

            int validBits = validLanes >= bv ? -1 : (1 << (validLanes * 4)) - 1;
            passMask &= validBits;

            if (passMask == 0)
                continue;

            Vector256<int> full = partial;
            for (int kp = earlyExitDimPairs; kp < dimPairs; kp++)
            {
                var v = Avx2.LoadVector256(blockPtr + kp * 16).AsInt16();
                var diff = Avx2.Subtract(v, qBroadcast[kp]);
                full = Avx2.Add(full, Avx2.MultiplyAddAdjacent(diff, diff));
            }

            full.CopyTo(dists);

            for (int v = 0; v < validLanes; v++)
            {
                int laneBits = 0xF << (v * 4);
                if ((passMask & laneBits) == 0) continue;

                int dist = dists[v];
                int vecIdx = offset + b * bv + v;

                if (heapSize < k)
                    HeapPush(heapIdx, heapDist, ref heapSize, vecIdx, dist);
                else if (dist < heapDist[0])
                    HeapReplaceTop(heapIdx, heapDist, k, vecIdx, dist);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScanClusterScalar(short* qInt, short* clusterVecBase, int offset, int count, int numBlocks,
        int* heapIdx, int* heapDist, ref int heapSize, int k)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        const int bv = IvfBinaryFormat.BlockVectors;
        const int blockShorts = pd * bv;
        const int dimPairs = pd / 2;

        for (int b = 0; b < numBlocks; b++)
        {
            short* blockPtr = clusterVecBase + b * blockShorts;
            int validLanes = Math.Min(bv, count - b * bv);

            for (int v = 0; v < validLanes; v++)
            {
                int dist = 0;
                for (int kp = 0; kp < dimPairs; kp++)
                {
                    int diff0 = qInt[2 * kp]     - blockPtr[kp * 16 + v * 2];
                    int diff1 = qInt[2 * kp + 1] - blockPtr[kp * 16 + v * 2 + 1];
                    dist += diff0 * diff0 + diff1 * diff1;
                }

                int vecIdx = offset + b * bv + v;
                if (heapSize < k)
                    HeapPush(heapIdx, heapDist, ref heapSize, vecIdx, dist);
                else if (dist < heapDist[0])
                    HeapReplaceTop(heapIdx, heapDist, k, vecIdx, dist);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UnpackFloat(int vecIdx, float* dest, int pd, int scale)
    {
        const int bv = IvfBinaryFormat.BlockVectors;
        const int laneStride = 2 * bv;
        int blockId  = vecIdx / bv;
        int laneId   = vecIdx - blockId * bv;
        short* blockPtr = _vectors + blockId * pd * bv;
        float invScale = 1.0f / scale;
        for (int kp = 0; kp < pd / 2; kp++)
        {
            short s0 = blockPtr[kp * laneStride + laneId * 2];
            short s1 = blockPtr[kp * laneStride + laneId * 2 + 1];
            dest[2 * kp]     = s0 * invScale;
            dest[2 * kp + 1] = s1 * invScale;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SiftDownFloat(Span<int> idx, Span<float> dist, int size, int i)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SiftDownProbe(int* list, float* dists, int size, int i)
    {
        while (true)
        {
            int largest = i, left = 2 * i + 1, right = 2 * i + 2;
            if (left  < size && dists[left]  > dists[largest]) largest = left;
            if (right < size && dists[right] > dists[largest]) largest = right;
            if (largest == i) break;
            (list[largest], list[i]) = (list[i], list[largest]);
            (dists[largest], dists[i]) = (dists[i], dists[largest]);
            i = largest;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HeapPush(int* idx, int* dist, ref int size, int vectorIndex, int d)
    {
        int i = size++;
        idx[i] = vectorIndex;
        dist[i] = d;
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
    private static void HeapReplaceTop(int* idx, int* dist, int size, int vectorIndex, int d)
    {
        idx[0] = vectorIndex;
        dist[0] = d;
        int i = 0;
        while (true)
        {
            int largest = i, left = 2 * i + 1, right = 2 * i + 2;
            if (left  < size && dist[left]  > dist[largest]) largest = left;
            if (right < size && dist[right] > dist[largest]) largest = right;
            if (largest == i) break;
            (idx[largest], idx[i]) = (idx[i], idx[largest]);
            (dist[largest], dist[i]) = (dist[i], dist[largest]);
            i = largest;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HeapPopKnn(int* idx, int* dist, ref int size)
    {
        int last = --size;
        idx[0] = idx[last];
        dist[0] = dist[last];
        if (size <= 0) return;
        int i = 0;
        while (true)
        {
            int largest = i, left = 2 * i + 1, right = 2 * i + 2;
            if (left  < size && dist[left]  > dist[largest]) largest = left;
            if (right < size && dist[right] > dist[largest]) largest = right;
            if (largest == i) break;
            (idx[largest], idx[i]) = (idx[i], idx[largest]);
            (dist[largest], dist[i]) = (dist[i], dist[largest]);
            i = largest;
        }
    }

    public void Dispose()
    {
        _exactAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        _exactAccessor?.Dispose();
        _exactMmf?.Dispose();
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
    }
}

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
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

    private readonly short* _centroids;
    private readonly sbyte* _centroidsT8;
    private readonly sbyte* _bboxMin8;
    private readonly sbyte* _bboxMax8;
    private readonly uint* _clusterRadius;
    private readonly uint* _clusterRadius8;
    private readonly IvfBinaryFormat.ClusterMeta* _clusterMeta;
    private readonly sbyte* _vectors;
    private readonly byte* _labels;
    private readonly int* _originalIndices;

    // v9: profile fast path. _profileMask[k] = 0|1|2|3 (legit/fraud/mixed
    // bitset), _profileCount[k] = ushort training-sample count. Habilitado
    // por env PROFILE_FAST_PATH (default 1). Threshold mínimo via
    // PROFILE_MIN_COUNT (default 30).
    private readonly byte* _profileMask;
    private readonly ushort* _profileCount;
    private readonly bool _profileEnabled;
    private readonly int _profileMinCount;
    private readonly bool _unanimitySkip;

    public int NumVectors { get; }
    public int NumClusters { get; }
    public int TotalSlots { get; }
    public int NprobeFast { get; }
    public int NprobeFull { get; }
    public string Description => $"IVF v10 SoA-block {NumClusters} clusters, int8 vectors + int8 centroids scan + profile-fastpath(min={_profileMinCount},enabled={_profileEnabled}), nprobe={NprobeFull}";

    /// <summary>
    /// Counter usado em testes/diagnóstico. Incrementado cada vez que
    /// o bbox-repair pass-2 é executado. Em produção, com queries
    /// não-borderline, fica abaixo do total de queries.
    /// </summary>
    public static int Pass2InvocationsForTest;

    /// <summary>
    /// Contadores de cobertura do profile fast path (dev/diagnóstico).
    /// Updated mesmo em prod (custo: 2 incrementos atomic-free) — útil
    /// pra calibrar PROFILE_MIN_COUNT e auditar drift entre training
    /// distribution e tráfego real.
    /// </summary>
    public static long ProfileFastPathLegitHits;
    public static long ProfileFastPathFraudHits;
    public static long ProfileFastPathMisses;

    /// <summary>
    /// Quantos dim-pairs a pass de early-exit do ScanClusterAvx2 acumula
    /// antes de comparar com o threshold worstDist. Default 4 (= metade dos
    /// 8 dim-pairs em D=14 padded 16). Override via env EARLY_EXIT_DIMS pra
    /// A/B sem rebuild. Faixa válida [1, 8].
    /// </summary>
    private static readonly int s_earlyExitDimPairs = ParseEarlyExitDims();

    private static int ParseEarlyExitDims()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("EARLY_EXIT_DIMS"), out var v)
            && v >= 1 && v <= 8)
            return v;
        return 4;
    }

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

        _centroids = (short*)(_basePtr + IvfBinaryFormat.CentroidsOffset);
        _centroidsT8 = AllocAndTransposeCentroidsInt8(_centroids, numClusters, IvfBinaryFormat.PaddedDims);
        short* bboxMin16 = (short*)(_basePtr + IvfBinaryFormat.BboxMinOffset(numClusters));
        short* bboxMax16 = (short*)(_basePtr + IvfBinaryFormat.BboxMaxOffset(numClusters));
        _bboxMin8 = AllocConvertInt16ToInt8(bboxMin16, numClusters * IvfBinaryFormat.PaddedDims);
        _bboxMax8 = AllocConvertInt16ToInt8(bboxMax16, numClusters * IvfBinaryFormat.PaddedDims);
        _clusterRadius = (uint*)(_basePtr + IvfBinaryFormat.ClusterRadiusOffset(numClusters));
        _clusterRadius8 = AllocScaleRadius(_clusterRadius, numClusters);
        _clusterMeta = (IvfBinaryFormat.ClusterMeta*)(_basePtr + IvfBinaryFormat.ClusterMetaOffset(numClusters));
        _vectors = (sbyte*)(_basePtr + IvfBinaryFormat.VectorsOffset(numClusters));
        _labels = _basePtr + IvfBinaryFormat.LabelsOffset(numClusters, totalSlots);
        _originalIndices = (int*)(_basePtr + IvfBinaryFormat.OriginalIndicesOffset(numClusters, totalSlots));
        _profileMask = _basePtr + IvfBinaryFormat.ProfileMaskOffset(numClusters, totalSlots);
        _profileCount = (ushort*)(_basePtr + IvfBinaryFormat.ProfileCountOffset(numClusters, totalSlots));

        _profileEnabled = (Environment.GetEnvironmentVariable("PROFILE_FAST_PATH") ?? "1") != "0";
        _profileMinCount = int.TryParse(Environment.GetEnvironmentVariable("PROFILE_MIN_COUNT"), out var pmc) && pmc > 0
            ? pmc : 30;
        _unanimitySkip = Environment.GetEnvironmentVariable("UNANIMITY_SKIP") == "1";
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
    /// Achieves 100% agreement with the ExactDetector reference implementation.
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
        // Linux-only mmap hints. Hugepages compress the IVF index (~110 MB,
        // ~28 k 4 KB pages) into ~55 2 MB pages — fits entirely in the Haswell
        // L1 dTLB (64 entries). WillNeed kicks readahead before the prefault
        // touch loop. Both are no-ops on macOS / non-Linux hosts.
        MmapHints.HintHugePages(_basePtr, totalSize);
        MmapHints.HintWillNeed(_basePtr, totalSize);
        for (long i = 0; i < totalSize; i += 4096)
            _ = _basePtr[i];

        if (_exactVectors != null)
        {
            // Walk the float32 reference data so the first real query doesn't
            // pay random page-fault cost during the rerank stage.
            const int pd = IvfBinaryFormat.PaddedDims;
            long exactBytes = (long)NumVectors * pd * sizeof(float);
            byte* exactBase = (byte*)_exactVectors;
            // Random access (rerank touches ~6 vectors anywhere in the file
            // per query). Disable readahead, request hugepages.
            MmapHints.HintRandom(exactBase, exactBytes);
            MmapHints.HintHugePages(exactBase, exactBytes);
            for (long i = 0; i < exactBytes; i += 4096)
                _ = exactBase[i];
        }
    }

    /// <summary>
    /// Per-stage timing layout for ScoreWithTimings: 4 longs = ticks for
    /// [centroidScan, initialClusterScan, bboxRepairPass, float32Rerank].
    /// </summary>
    public const int TimingsCount = 4;

    /// <summary>
    /// Pass-2 cascade counter layout: [trianglePruned, bboxPruned, scanned].
    /// Together they sum to (numClusters - probeCount) — every non-probed
    /// cluster ends in exactly one bucket.
    /// </summary>
    public const int CountersCount = 3;

    public (bool Approved, int FraudCount) Score(ReadOnlySpan<float> query) =>
        ScoreCore(query, ticksOut: null, countsOut: null);

    /// <summary>
    /// Dev-only path. Same algorithm as <see cref="Score"/>, but writes
    /// per-stage tick counts to <paramref name="ticksOut"/> (length 4) and
    /// pass-2 cluster fate counts to <paramref name="countsOut"/> (length 3).
    /// Either pointer may be null to skip that side.
    /// </summary>
    public (bool Approved, int FraudCount) ScoreWithTimings(ReadOnlySpan<float> query, long* ticksOut, int* countsOut = null)
        => ScoreCore(query, ticksOut, countsOut);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (bool Approved, int FraudCount) ScoreCore(ReadOnlySpan<float> query, long* ticksOut, int* countsOut)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        const int scale = IvfBinaryFormat.Scale;
        const int int8Scale = IvfBinaryFormat.Int8Scale;
        const int rerankN = 6;

        // int16-scale query for profile fast path (uses Scale=4096 thresholds)
        short* qInt16 = stackalloc short[pd];
        for (int d = 0; d < pd; d++)
        {
            float v = MathF.Round(query[d] * scale);
            if (v > short.MaxValue) qInt16[d] = short.MaxValue;
            else if (v < short.MinValue) qInt16[d] = short.MinValue;
            else qInt16[d] = (short)v;
        }

        if (_profileEnabled && ticksOut == null)
        {
            int pkey = ProfileFastPath.Key(qInt16);
            byte pmask = _profileMask[pkey];
            if (pmask == IvfBinaryFormat.ProfileLegitMask
                && _profileCount[pkey] >= _profileMinCount)
            {
                ProfileFastPathLegitHits++;
                return (true, 0);
            }
            if (pmask == IvfBinaryFormat.ProfileFraudMask
                && _profileCount[pkey] >= _profileMinCount)
            {
                ProfileFastPathFraudHits++;
                return (false, 5);
            }
            ProfileFastPathMisses++;
        }

        // int8-scale query for IVF search (all distances in int8² space)
        short* qInt8 = stackalloc short[pd];
        for (int d = 0; d < pd; d++)
        {
            float v = MathF.Round(query[d] * int8Scale);
            if (v > 127) qInt8[d] = 127;
            else if (v < -128) qInt8[d] = -128;
            else qInt8[d] = (short)v;
        }

        int* qPairs = stackalloc int[pd / 2];
        for (int kp = 0; kp < pd / 2; kp++)
            qPairs[kp] = ((ushort)qInt8[2 * kp]) | ((ushort)qInt8[2 * kp + 1] << 16);

        Span<int> candidateIdx = stackalloc int[rerankN];

        int candidateCount = FindKNearest(qInt8, qPairs, rerankN, candidateIdx,
                                          NprobeFull, useBboxRepair: true, ticksOut, countsOut);

        long s2Start = ticksOut != null ? Stopwatch.GetTimestamp() : 0;

        int topK = Math.Min(candidateCount, 5);
        int fraudCount = 0;
        for (int i = 0; i < topK; i++)
            fraudCount += _labels[candidateIdx[i]];

        if (ticksOut != null)
            ticksOut[3] = Stopwatch.GetTimestamp() - s2Start;

        return (fraudCount < 3, fraudCount);
    }

    private int FindKNearest(short* qInt8, int* qPairs, int k, Span<int> resultIdx,
        int nprobe, bool useBboxRepair, long* ticksOut = null, int* countsOut = null)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        int numClusters = NumClusters;
        int actualNprobe = Math.Min(nprobe, numClusters);

        int* probeList  = stackalloc int[actualNprobe];
        int* probeDists = stackalloc int[actualNprobe];
        int probeCount = 0;
        int* centroidDist = stackalloc int[numClusters];

        long t0 = ticksOut != null ? Stopwatch.GetTimestamp() : 0;

        SimdDistance.SByteL2SquaredAllDimMajor(qInt8, _centroidsT8, centroidDist, numClusters, pd);

        // Walk K distances and build the top-actualNprobe min-heap (by
        // largest at root, so a new smaller dist can replace root).
        for (int c = 0; c < numClusters; c++)
        {
            int dist = centroidDist[c];
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
        {
            int cId = probeList[p];
            if (heapSize == k)
            {
                int lb = SimdDistance.SByteBboxLowerBound(qInt8, _bboxMin8 + cId * pd, _bboxMax8 + cId * pd);
                if (lb > heapDist[0]) continue;
            }
            ScanCluster(qInt8, qPairs, cId, heapIdx, heapDist, ref heapSize, k);
        }

        long t2 = ticksOut != null ? Stopwatch.GetTimestamp() : 0;
        if (ticksOut != null) ticksOut[1] = t2 - t1;

        // Unanimity early exit: if all K candidates from the initial probe
        // share the same label, skip the expensive bbox repair pass. The
        // decision (approved/denied) cannot change regardless of what the
        // repair pass might find. Toggle via UNANIMITY_SKIP=1.
        if (_unanimitySkip && useBboxRepair && probeCount < numClusters && heapSize == k)
        {
            int fc = 0;
            for (int i = 0; i < heapSize; i++)
                fc += _labels[heapIdx[i]];
            if (fc == 0 || fc == heapSize)
            {
                if (ticksOut != null) ticksOut[2] = 0;
                goto extractResults;
            }
        }

        if (useBboxRepair && probeCount < numClusters)
        {
            Pass2InvocationsForTest++;

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
            long sqrtWorstCeil = heapSize == k ? (long)Math.Ceiling(Math.Sqrt(worstDist)) : long.MaxValue;

            // Two-phase pass-2: (1) triangle-only sweep collects survivors;
            // (2) scan survivors in centroidDist-ascending order so the heap
            // converges to tight worstDist fast, pruning more downstream.
            int* survivorBuf = stackalloc int[numClusters];
            int survivorCount = 0;

            bool vEnabled = Avx2.IsSupported && sqrtWorstCeil != long.MaxValue;
            Vector256<long> vSqrtWorst = vEnabled ? Vector256.Create(sqrtWorstCeil) : Vector256<long>.Zero;
            const int Lanes = 4;

            for (int cBase = 0; cBase < numClusters; cBase += Lanes)
            {
                int scannedMask = (int)((scannedBits[cBase >> 6] >> (cBase & 63)) & 0xF);
                int survivorMask;

                if (vEnabled)
                {
                    var cd128 = Sse2.LoadVector128(centroidDist + cBase);
                    var rad128 = Sse2.LoadVector128((int*)(_clusterRadius8 + cBase));
                    var cdL = Avx2.ConvertToVector256Int64(cd128);
                    var radL = Avx2.ConvertToVector256Int64(rad128);
                    var threshL = Avx2.Add(radL, vSqrtWorst);
                    var threshAsInt = threshL.AsInt32();
                    var threshSqL = Avx2.Multiply(threshAsInt, threshAsInt);
                    var cmp = Avx2.CompareGreaterThan(cdL, threshSqL);
                    int triSkipMask = Avx.MoveMask(cmp.AsDouble()) & 0xF;

                    int triPrunedNotScanned = triSkipMask & ~scannedMask;
                    if (countsOut != null) countsOut[0] += BitOperations.PopCount((uint)triPrunedNotScanned);
                    survivorMask = ~(triSkipMask | scannedMask) & 0xF;
                }
                else
                {
                    survivorMask = ~scannedMask & 0xF;
                }

                while (survivorMask != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(survivorMask);
                    survivorMask &= survivorMask - 1;
                    int c = cBase + bit;
                    survivorBuf[survivorCount] = c;
                    survivorCount++;
                }
            }

            // Shell sort survivors by centroidDist ascending. O(n^{4/3}) worst case,
            // fast for typical ~100-500 survivors. Ciura gap sequence.
            ReadOnlySpan<int> gaps = [301, 132, 57, 23, 10, 4, 1];
            foreach (int gap in gaps)
            {
                if (gap >= survivorCount) continue;
                for (int i = gap; i < survivorCount; i++)
                {
                    int val = survivorBuf[i];
                    int key = centroidDist[val];
                    int j = i - gap;
                    while (j >= 0 && centroidDist[survivorBuf[j]] > key)
                    {
                        survivorBuf[j + gap] = survivorBuf[j];
                        j -= gap;
                    }
                    survivorBuf[j + gap] = val;
                }
            }

            for (int s = 0; s < survivorCount; s++)
            {
                int c = survivorBuf[s];

                if (heapSize == k)
                {
                    long sw = sqrtWorstCeil;
                    long thresh = (long)_clusterRadius8[c] + sw;
                    if ((long)centroidDist[c] > thresh * thresh)
                    {
                        if (countsOut != null) countsOut[0]++;
                        continue;
                    }
                }

                int lb = SimdDistance.SByteBboxLowerBound(qInt8, _bboxMin8 + c * pd, _bboxMax8 + c * pd);
                if (lb <= worstDist)
                {
                    if (countsOut != null) countsOut[2]++;
                    ScanCluster(qInt8, qPairs, c, heapIdx, heapDist, ref heapSize, k);
                    if (heapSize == k)
                    {
                        int newWorst = heapDist[0];
                        if (newWorst != worstDist)
                        {
                            worstDist = newWorst;
                            sqrtWorstCeil = (long)Math.Ceiling(Math.Sqrt(worstDist));
                        }
                    }
                }
                else if (countsOut != null) countsOut[1]++;
            }
        }

        if (ticksOut != null) ticksOut[2] = Stopwatch.GetTimestamp() - t2;

    extractResults:
        int resultCount = heapSize;
        for (int i = heapSize - 1; i >= 0; i--)
        {
            resultIdx[i] = heapIdx[0];
            HeapPopKnn(heapIdx, heapDist, ref heapSize);
        }
        return resultCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ScanCluster(short* qInt8, int* qPairs, int clusterId,
        int* heapIdx, int* heapDist, ref int heapSize, int k)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        const int bv = IvfBinaryFormat.BlockVectors;

        ref var meta = ref _clusterMeta[clusterId];
        int offset = (int)meta.Offset;
        int count = (int)meta.Count;
        if (count == 0) return;

        int numBlocks = (count + bv - 1) / bv;
        sbyte* clusterVecBase = _vectors + offset * pd;

        if (Avx2.IsSupported)
        {
            ScanClusterAvx2(qPairs, clusterVecBase, offset, count, numBlocks, heapIdx, heapDist, ref heapSize, k);
        }
        else
        {
            ScanClusterScalar(qInt8, clusterVecBase, offset, count, numBlocks, heapIdx, heapDist, ref heapSize, k);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScanClusterAvx2(int* qPairs, sbyte* clusterVecBase, int offset, int count, int numBlocks,
        int* heapIdx, int* heapDist, ref int heapSize, int k)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        const int bv = IvfBinaryFormat.BlockVectors;
        const int blockBytes = pd * bv;
        const int dimPairs = pd / 2;
        int earlyExitDimPairs = s_earlyExitDimPairs;
        int earlyExit1 = earlyExitDimPairs >> 1;

        Span<Vector256<short>> qBroadcast = stackalloc Vector256<short>[dimPairs];
        for (int kp = 0; kp < dimPairs; kp++)
            qBroadcast[kp] = Vector256.Create(qPairs[kp]).AsInt16();

        Span<int> dists = stackalloc int[bv];

        int b = 0;
        for (; b + 1 < numBlocks; b += 2)
        {
            sbyte* blockPtr0 = clusterVecBase + b * blockBytes;

            if (b + 4 < numBlocks)
                Sse.Prefetch0(clusterVecBase + (b + 4) * blockBytes);

            int validLanes0 = count - b * bv;
            if (validLanes0 > bv) validLanes0 = bv;
            int validLanes1 = count - (b + 1) * bv;
            if (validLanes1 > bv) validLanes1 = bv;

            Vector256<int> acc0a = Vector256<int>.Zero;
            Vector256<int> acc0b = Vector256<int>.Zero;
            Vector256<int> acc1a = Vector256<int>.Zero;
            Vector256<int> acc1b = Vector256<int>.Zero;

            for (int kp = 0; kp < earlyExit1; kp += 2)
            {
                var v0 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + kp * 16));
                var v1 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + blockBytes + kp * 16));
                var diff0 = Avx2.Subtract(v0, qBroadcast[kp]);
                var diff1 = Avx2.Subtract(v1, qBroadcast[kp]);
                acc0a = Avx2.Add(acc0a, Avx2.MultiplyAddAdjacent(diff0, diff0));
                acc1a = Avx2.Add(acc1a, Avx2.MultiplyAddAdjacent(diff1, diff1));

                if (kp + 1 < earlyExit1)
                {
                    var v0b = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + (kp + 1) * 16));
                    var v1b = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + blockBytes + (kp + 1) * 16));
                    var diff0b = Avx2.Subtract(v0b, qBroadcast[kp + 1]);
                    var diff1b = Avx2.Subtract(v1b, qBroadcast[kp + 1]);
                    acc0b = Avx2.Add(acc0b, Avx2.MultiplyAddAdjacent(diff0b, diff0b));
                    acc1b = Avx2.Add(acc1b, Avx2.MultiplyAddAdjacent(diff1b, diff1b));
                }
            }

            if (heapSize == k)
            {
                var threshold1 = Vector256.Create(heapDist[0]);
                var p0 = Avx2.Add(acc0a, acc0b);
                var p1 = Avx2.Add(acc1a, acc1b);
                var lt0 = Avx2.CompareGreaterThan(threshold1, p0);
                var lt1 = Avx2.CompareGreaterThan(threshold1, p1);
                int validBitsE0 = validLanes0 >= bv ? -1 : (1 << (validLanes0 * 4)) - 1;
                int validBitsE1 = validLanes1 >= bv ? -1 : (1 << (validLanes1 * 4)) - 1;
                int eMask = (Avx2.MoveMask(lt0.AsByte()) & validBitsE0)
                          | (Avx2.MoveMask(lt1.AsByte()) & validBitsE1);
                if (eMask == 0)
                    continue;
            }

            for (int kp = earlyExit1; kp < earlyExitDimPairs; kp += 2)
            {
                var v0 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + kp * 16));
                var v1 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + blockBytes + kp * 16));
                var diff0 = Avx2.Subtract(v0, qBroadcast[kp]);
                var diff1 = Avx2.Subtract(v1, qBroadcast[kp]);
                acc0a = Avx2.Add(acc0a, Avx2.MultiplyAddAdjacent(diff0, diff0));
                acc1a = Avx2.Add(acc1a, Avx2.MultiplyAddAdjacent(diff1, diff1));

                if (kp + 1 < earlyExitDimPairs)
                {
                    var v0b = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + (kp + 1) * 16));
                    var v1b = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + blockBytes + (kp + 1) * 16));
                    var diff0b = Avx2.Subtract(v0b, qBroadcast[kp + 1]);
                    var diff1b = Avx2.Subtract(v1b, qBroadcast[kp + 1]);
                    acc0b = Avx2.Add(acc0b, Avx2.MultiplyAddAdjacent(diff0b, diff0b));
                    acc1b = Avx2.Add(acc1b, Avx2.MultiplyAddAdjacent(diff1b, diff1b));
                }
            }

            Vector256<int> partial0 = Avx2.Add(acc0a, acc0b);
            Vector256<int> partial1 = Avx2.Add(acc1a, acc1b);

            int worst = (heapSize == k) ? heapDist[0] : int.MaxValue;
            var threshold = Vector256.Create(worst);

            var ltMask0 = Avx2.CompareGreaterThan(threshold, partial0);
            int passMask0 = Avx2.MoveMask(ltMask0.AsByte());
            int validBits0 = validLanes0 >= bv ? -1 : (1 << (validLanes0 * 4)) - 1;
            passMask0 &= validBits0;

            var ltMask1 = Avx2.CompareGreaterThan(threshold, partial1);
            int passMask1 = Avx2.MoveMask(ltMask1.AsByte());
            int validBits1 = validLanes1 >= bv ? -1 : (1 << (validLanes1 * 4)) - 1;
            passMask1 &= validBits1;

            if ((passMask0 | passMask1) == 0)
                continue;

            if (passMask0 != 0)
            {
                Vector256<int> full0 = partial0;
                for (int kp = earlyExitDimPairs; kp < dimPairs; kp += 2)
                {
                    var v0 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + kp * 16));
                    var diff0 = Avx2.Subtract(v0, qBroadcast[kp]);
                    full0 = Avx2.Add(full0, Avx2.MultiplyAddAdjacent(diff0, diff0));
                    if (kp + 1 < dimPairs)
                    {
                        var v0b = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + (kp + 1) * 16));
                        var diff0b = Avx2.Subtract(v0b, qBroadcast[kp + 1]);
                        full0 = Avx2.Add(full0, Avx2.MultiplyAddAdjacent(diff0b, diff0b));
                    }
                }
                full0.CopyTo(dists);

                uint laneMask = Bmi2.ParallelBitExtract((uint)passMask0, 0x11111111u);
                while (laneMask != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(laneMask);
                    int dist = dists[lane];
                    int vecIdx = offset + b * bv + lane;
                    if (heapSize < k)
                        HeapPush(heapIdx, heapDist, ref heapSize, vecIdx, dist);
                    else if (dist < heapDist[0])
                        HeapReplaceTop(heapIdx, heapDist, k, vecIdx, dist);
                    laneMask &= laneMask - 1;
                }
            }

            if (passMask1 != 0)
            {
                Vector256<int> full1 = partial1;
                for (int kp = earlyExitDimPairs; kp < dimPairs; kp += 2)
                {
                    var v1 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + blockBytes + kp * 16));
                    var diff1 = Avx2.Subtract(v1, qBroadcast[kp]);
                    full1 = Avx2.Add(full1, Avx2.MultiplyAddAdjacent(diff1, diff1));
                    if (kp + 1 < dimPairs)
                    {
                        var v1b = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr0 + blockBytes + (kp + 1) * 16));
                        var diff1b = Avx2.Subtract(v1b, qBroadcast[kp + 1]);
                        full1 = Avx2.Add(full1, Avx2.MultiplyAddAdjacent(diff1b, diff1b));
                    }
                }
                full1.CopyTo(dists);

                uint laneMask = Bmi2.ParallelBitExtract((uint)passMask1, 0x11111111u);
                while (laneMask != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(laneMask);
                    int dist = dists[lane];
                    int vecIdx = offset + (b + 1) * bv + lane;
                    if (heapSize < k)
                        HeapPush(heapIdx, heapDist, ref heapSize, vecIdx, dist);
                    else if (dist < heapDist[0])
                        HeapReplaceTop(heapIdx, heapDist, k, vecIdx, dist);
                    laneMask &= laneMask - 1;
                }
            }
        }

        if (b < numBlocks)
        {
            sbyte* blockPtr = clusterVecBase + b * blockBytes;
            int validLanes = count - b * bv;
            if (validLanes > bv) validLanes = bv;

            Vector256<int> accA = Vector256<int>.Zero;
            Vector256<int> accB = Vector256<int>.Zero;
            for (int kp = 0; kp < earlyExitDimPairs; kp += 2)
            {
                var v = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr + kp * 16));
                var diff = Avx2.Subtract(v, qBroadcast[kp]);
                accA = Avx2.Add(accA, Avx2.MultiplyAddAdjacent(diff, diff));
                if (kp + 1 < earlyExitDimPairs)
                {
                    var vb = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr + (kp + 1) * 16));
                    var diffb = Avx2.Subtract(vb, qBroadcast[kp + 1]);
                    accB = Avx2.Add(accB, Avx2.MultiplyAddAdjacent(diffb, diffb));
                }
            }
            Vector256<int> partial = Avx2.Add(accA, accB);

            int worst = (heapSize == k) ? heapDist[0] : int.MaxValue;
            var threshold = Vector256.Create(worst);
            var ltMask = Avx2.CompareGreaterThan(threshold, partial);
            int passMask = Avx2.MoveMask(ltMask.AsByte());
            int validBits = validLanes >= bv ? -1 : (1 << (validLanes * 4)) - 1;
            passMask &= validBits;

            if (passMask != 0)
            {
                Vector256<int> full = partial;
                for (int kp = earlyExitDimPairs; kp < dimPairs; kp += 2)
                {
                    var v = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr + kp * 16));
                    var diff = Avx2.Subtract(v, qBroadcast[kp]);
                    full = Avx2.Add(full, Avx2.MultiplyAddAdjacent(diff, diff));
                    if (kp + 1 < dimPairs)
                    {
                        var vb = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(blockPtr + (kp + 1) * 16));
                        var diffb = Avx2.Subtract(vb, qBroadcast[kp + 1]);
                        full = Avx2.Add(full, Avx2.MultiplyAddAdjacent(diffb, diffb));
                    }
                }
                full.CopyTo(dists);

                uint laneMask = Bmi2.ParallelBitExtract((uint)passMask, 0x11111111u);
                while (laneMask != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(laneMask);
                    int dist = dists[lane];
                    int vecIdx = offset + b * bv + lane;
                    if (heapSize < k)
                        HeapPush(heapIdx, heapDist, ref heapSize, vecIdx, dist);
                    else if (dist < heapDist[0])
                        HeapReplaceTop(heapIdx, heapDist, k, vecIdx, dist);
                    laneMask &= laneMask - 1;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScanClusterScalar(short* qInt8, sbyte* clusterVecBase, int offset, int count, int numBlocks,
        int* heapIdx, int* heapDist, ref int heapSize, int k)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        const int bv = IvfBinaryFormat.BlockVectors;
        const int blockBytes = pd * bv;
        const int dimPairs = pd / 2;

        for (int b = 0; b < numBlocks; b++)
        {
            sbyte* blockPtr = clusterVecBase + b * blockBytes;
            int validLanes = Math.Min(bv, count - b * bv);

            for (int v = 0; v < validLanes; v++)
            {
                int dist = 0;
                for (int kp = 0; kp < dimPairs; kp++)
                {
                    int diff0 = qInt8[2 * kp]     - blockPtr[kp * 16 + v * 2];
                    int diff1 = qInt8[2 * kp + 1] - blockPtr[kp * 16 + v * 2 + 1];
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
    private static void SiftDownProbe(int* list, int* dists, int size, int i)
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
        if (_centroidsT8 != null)
            System.Runtime.InteropServices.NativeMemory.AlignedFree(_centroidsT8);
        if (_bboxMin8 != null)
            System.Runtime.InteropServices.NativeMemory.AlignedFree(_bboxMin8);
        if (_bboxMax8 != null)
            System.Runtime.InteropServices.NativeMemory.AlignedFree(_bboxMax8);
        if (_clusterRadius8 != null)
            System.Runtime.InteropServices.NativeMemory.AlignedFree(_clusterRadius8);
        _exactAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        _exactAccessor?.Dispose();
        _exactMmf?.Dispose();
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
    }

    private static sbyte* AllocAndTransposeCentroidsInt8(short* centroids, int K, int pd)
    {
        const int shift = IvfBinaryFormat.Int16ToInt8Shift;
        nuint bytes = (nuint)((long)K * pd);
        sbyte* buf = (sbyte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(bytes, 64);
        int dimPairs = pd / 2;
        for (int c = 0; c < K; c++)
            for (int dp = 0; dp < dimPairs; dp++)
            {
                buf[(long)dp * K * 2 + c * 2]     = (sbyte)Math.Clamp(centroids[(long)c * pd + dp * 2] >> shift, -128, 127);
                buf[(long)dp * K * 2 + c * 2 + 1] = (sbyte)Math.Clamp(centroids[(long)c * pd + dp * 2 + 1] >> shift, -128, 127);
            }
        return buf;
    }

    private static sbyte* AllocConvertInt16ToInt8(short* src, int count)
    {
        const int shift = IvfBinaryFormat.Int16ToInt8Shift;
        nuint bytes = (nuint)count;
        sbyte* buf = (sbyte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(bytes, 64);
        for (int i = 0; i < count; i++)
            buf[i] = (sbyte)Math.Clamp(src[i] >> shift, -128, 127);
        return buf;
    }

    private static uint* AllocScaleRadius(uint* src, int count)
    {
        const int shift = IvfBinaryFormat.Int16ToInt8Shift;
        nuint bytes = (nuint)(count * sizeof(uint));
        uint* buf = (uint*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(bytes, 64);
        for (int i = 0; i < count; i++)
            buf[i] = (src[i] + (1u << shift) - 1) >> shift;
        return buf;
    }
}

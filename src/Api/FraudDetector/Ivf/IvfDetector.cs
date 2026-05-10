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
    // Centróides em layout dim-major (`_centroidsT[d * K + c]`) alocados em
    // heap unmanaged. Pré-computado em Open() a partir do mmap K-major.
    // Substitui a passagem K iterações × Int16L2Squared (com horizontal
    // reduce por cluster) por uma passagem dim-major com K acumuladores
    // int32 mantidos vector-form. Custo memória: K * pd * 2 = 128 KB (cabe
    // no L2 256 KB Haswell). Inspirado em jairoblatt/rinha-2026-rust.
    private readonly short* _centroidsT;
    private readonly short* _bboxMin;
    private readonly short* _bboxMax;
    private readonly uint* _clusterRadius; // v8: ceil(sqrt(max int16-sq dist)) per cluster
    private readonly IvfBinaryFormat.ClusterMeta* _clusterMeta;
    private readonly short* _vectors;
    private readonly byte* _labels;
    private readonly int* _originalIndices; // slot → original global index in exact.bin

    // v9: profile fast path. _profileMask[k] = 0|1|2|3 (legit/fraud/mixed
    // bitset), _profileCount[k] = ushort training-sample count. Habilitado
    // por env PROFILE_FAST_PATH (default 1). Threshold mínimo via
    // PROFILE_MIN_COUNT (default 30).
    private readonly byte* _profileMask;
    private readonly ushort* _profileCount;
    private readonly bool _profileEnabled;
    private readonly int _profileMinCount;

    public int NumVectors { get; }
    public int NumClusters { get; }
    public int TotalSlots { get; }
    public int NprobeFast { get; }
    public int NprobeFull { get; }
    public string Description => $"IVF v9 SoA-block {NumClusters} clusters, int16 centroids+vectors+radius + f32-rerank + profile-fastpath(min={_profileMinCount},enabled={_profileEnabled}), nprobe={NprobeFull}";

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
        _centroidsT = AllocAndTransposeCentroids(_centroids, numClusters, IvfBinaryFormat.PaddedDims);
        _bboxMin = (short*)(_basePtr + IvfBinaryFormat.BboxMinOffset(numClusters));
        _bboxMax = (short*)(_basePtr + IvfBinaryFormat.BboxMaxOffset(numClusters));
        _clusterRadius = (uint*)(_basePtr + IvfBinaryFormat.ClusterRadiusOffset(numClusters));
        _clusterMeta = (IvfBinaryFormat.ClusterMeta*)(_basePtr + IvfBinaryFormat.ClusterMetaOffset(numClusters));
        _vectors = (short*)(_basePtr + IvfBinaryFormat.VectorsOffset(numClusters));
        _labels = _basePtr + IvfBinaryFormat.LabelsOffset(numClusters, totalSlots);
        _originalIndices = (int*)(_basePtr + IvfBinaryFormat.OriginalIndicesOffset(numClusters, totalSlots));
        _profileMask = _basePtr + IvfBinaryFormat.ProfileMaskOffset(numClusters, totalSlots);
        _profileCount = (ushort*)(_basePtr + IvfBinaryFormat.ProfileCountOffset(numClusters, totalSlots));

        _profileEnabled = (Environment.GetEnvironmentVariable("PROFILE_FAST_PATH") ?? "1") != "0";
        _profileMinCount = int.TryParse(Environment.GetEnvironmentVariable("PROFILE_MIN_COUNT"), out var pmc) && pmc > 0
            ? pmc : 30;
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

    public (bool Approved, int FraudCount) Score(ReadOnlySpan<float> query)
        => ScoreCore(query, ticksOut: null, countsOut: null);

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
        // rerankN sweep (10 k synthetic queries, seed=195842629, NPROBE_FULL=5):
        //   32 → 0.193 ms, 100% agreement (was the prior baseline)
        //    8 → 0.141 ms, 100% agreement
        //    7 → 0.135 ms, 100% agreement
        //    6 → 0.132 ms, 100% agreement (chosen — minimum buffer that holds)
        //    5 → 0.263 ms, 99.81% (10 FP + 9 FN — no buffer = recall fails)
        const int rerankN = 6;

        short* qInt = stackalloc short[pd];
        for (int d = 0; d < pd; d++)
        {
            float v = MathF.Round(query[d] * scale);
            if (v > short.MaxValue) qInt[d] = short.MaxValue;
            else if (v < short.MinValue) qInt[d] = short.MinValue;
            else qInt[d] = (short)v;
        }

        // Profile fast path: bucket monocromático com count >= threshold
        // devolve a resposta sem tocar no IVF. Atalho de ~50 ns vs ~1 ms
        // do IVF completo. ticksOut == null evita overhead do timing
        // path; ticks dos estágios IVF ficam zerados quando o atalho
        // dispara, indicando "fast-path hit" no Server-Timing.
        if (_profileEnabled && ticksOut == null)
        {
            int pkey = ProfileFastPath.Key(qInt);
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

        int* qPairs = stackalloc int[pd / 2];
        for (int kp = 0; kp < pd / 2; kp++)
            qPairs[kp] = ((ushort)qInt[2 * kp]) | ((ushort)qInt[2 * kp + 1] << 16);

        Span<int>   candidateIdx = stackalloc int[rerankN];
        Span<int>   topIdx       = stackalloc int[5];
        Span<float> topDist      = stackalloc float[5];

        int fraudCount;
        fixed (float* qFloat = query)
        {
            int candidateCount = FindKNearest(qFloat, qInt, qPairs, rerankN, candidateIdx,
                                              NprobeFull, useBboxRepair: true, ticksOut, countsOut);

            long s2Start = ticksOut != null ? Stopwatch.GetTimestamp() : 0;

            float* exactVecs = _exactVectors;

            if (exactVecs != null)
            {
                // Float32 rerank using exact.bin — original float32 vectors
                // give genuine higher precision than dequantized int16.
                int* origIdx = _originalIndices;
                int topSize = 0;
                for (int i = 0; i < candidateCount; i++)
                {
                    int vecIdx = candidateIdx[i];
                    int origGlobalIdx = origIdx[vecIdx];
                    float dist = SimdDistance.EuclideanSquaredPtr(qFloat, exactVecs + (long)origGlobalIdx * pd);

                    if (topSize < 5)
                    {
                        int p = topSize - 1;
                        while (p >= 0 && topDist[p] > dist)
                        {
                            topDist[p + 1] = topDist[p];
                            topIdx[p + 1]  = topIdx[p];
                            p--;
                        }
                        topDist[p + 1] = dist;
                        topIdx[p + 1]  = vecIdx;
                        topSize++;
                    }
                    else if (dist < topDist[4])
                    {
                        int p = 3;
                        while (p >= 0 && topDist[p] > dist)
                        {
                            topDist[p + 1] = topDist[p];
                            topIdx[p + 1]  = topIdx[p];
                            p--;
                        }
                        topDist[p + 1] = dist;
                        topIdx[p + 1]  = vecIdx;
                    }
                }

                fraudCount = 0;
                for (int i = 0; i < topSize; i++)
                    fraudCount += _labels[topIdx[i]];
            }
            else
            {
                // Int16 ranking is monotonically equivalent to dequantized
                // float32 (dividing by scale² preserves order). Skip the
                // float-domain recompute and random mmap reads entirely.
                int topK = Math.Min(candidateCount, 5);
                fraudCount = 0;
                for (int i = 0; i < topK; i++)
                    fraudCount += _labels[candidateIdx[i]];
            }

            if (ticksOut != null)
                ticksOut[3] = Stopwatch.GetTimestamp() - s2Start;
        }

        return (fraudCount < 3, fraudCount);
    }

    private int FindKNearest(float* qFloat, short* qInt, int* qPairs, int k, Span<int> resultIdx,
        int nprobe, bool useBboxRepair, long* ticksOut = null, int* countsOut = null)
    {
        const int pd = IvfBinaryFormat.PaddedDims;
        int numClusters = NumClusters;
        int actualNprobe = Math.Min(nprobe, numClusters);

        int* probeList  = stackalloc int[actualNprobe];
        int* probeDists = stackalloc int[actualNprobe];
        int probeCount = 0;
        // v8: cache the int16-squared centroid distance per cluster so the
        // pass-2 triangle-inequality skip can reuse it without recomputing
        // any SIMD work. 4096 clusters * 4 B = 16 KB on the call stack.
        int* centroidDist = stackalloc int[numClusters];

        long t0 = ticksOut != null ? Stopwatch.GetTimestamp() : 0;

        // Compute all K centroid distances in a single dim-major SIMD pass.
        // Substitui o loop K × Int16L2Squared (horizontal reduce por
        // cluster). Working-set por dim = K * 2 = 8 KB, cabe em L1
        // Haswell (32 KB). Padding dims [D..pd) zeros não contribuem.
        SimdDistance.Int16L2SquaredAllDimMajor(qInt, _centroidsT, centroidDist, numClusters, pd);

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
            // Once the K-heap is full, skip a probed cluster whose bbox lower
            // bound already exceeds the worst-K. Saves a full ScanCluster
            // (~3–5 µs) at the cost of a single SIMD lb compute (~50 ns).
            if (heapSize == k)
            {
                int lb = SimdDistance.Int16BboxLowerBound(qInt, _bboxMin + cId * pd, _bboxMax + cId * pd);
                if (lb > heapDist[0]) continue;
            }
            ScanCluster(qInt, qPairs, cId, heapIdx, heapDist, ref heapSize, k);
        }

        long t2 = ticksOut != null ? Stopwatch.GetTimestamp() : 0;
        if (ticksOut != null) ticksOut[1] = t2 - t1;

        if (useBboxRepair && probeCount < numClusters)
        {
            // Tentativa de conditional bbox-repair (zan trick) revertida em
            // 2026-05-08: com NPROBE_FULL=5 a pass-1 amostra apenas 5 dos
            // 4096 clusters; em queries random, a sample não-representativa
            // produz p1Fraud=0 ou p1Fraud=5,6 mesmo quando o oracle discorda.
            // Faixa borderline {1,2,3,4} produziu 73 mismatches em 10k
            // queries (0.73%), quebrando o requisito de 100% acurácia.
            // O atalho exigiria NPROBE_FULL=20+ pra ter margem de pass-1.
            // Mantida pass-2 incondicional. Pass2InvocationsForTest hoje
            // sempre = total de queries.
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
            // Triangle bound: skip cluster c if d(q,C_c)² > (sqrt(worstDist) + r_c)².
            // Reuses the centroidDist[c] computed above. Cheaper than the bbox-LB
            // SIMD compute, so cascade triangle → bbox → ScanCluster. sqrtWorst
            // is recomputed only when worstDist actually drops (heap-top update),
            // which is rare relative to the 4096-cluster walk.
            long sqrtWorstCeil = heapSize == k ? (long)Math.Ceiling(Math.Sqrt(worstDist)) : long.MaxValue;

            // Vectorize triangle-skip across 4 clusters at a time. int64
            // arithmetic to avoid thresh² overflow (worst-case ~92K² ≈ 8.5e9
            // exceeds int32). Avx2.Multiply(int32,int32)→int64 multiplies
            // even-indexed lanes; ConvertToVector256Int64 places each 32-bit
            // thresh in the even slot with zero high half, so the product is
            // (t_i)² as int64. The 4096-cluster loop is the dominant
            // pass-2 cost; this collapses ~14µs of scalar bookkeeping on
            // Haswell into ~7µs of vector work.
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
                    var rad128 = Sse2.LoadVector128((int*)(_clusterRadius + cBase));
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

                    int lb = SimdDistance.Int16BboxLowerBound(qInt, _bboxMin + c * pd, _bboxMax + c * pd);
                    if (lb <= worstDist)
                    {
                        if (countsOut != null) countsOut[2]++;
                        ScanCluster(qInt, qPairs, c, heapIdx, heapDist, ref heapSize, k);
                        if (heapSize == k)
                        {
                            int newWorst = heapDist[0];
                            if (newWorst != worstDist)
                            {
                                worstDist = newWorst;
                                sqrtWorstCeil = (long)Math.Ceiling(Math.Sqrt(worstDist));
                                if (Avx2.IsSupported)
                                {
                                    vSqrtWorst = Vector256.Create(sqrtWorstCeil);
                                    vEnabled = true;
                                }
                            }
                        }
                    }
                    else if (countsOut != null) countsOut[1]++;
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
        // Lê o static readonly numa local pra forçar hoist do load fora dos
        // loops (JIT pode não confiar em static readonly como invariante
        // dentro de hot path). Custo: 1 ldsfld no setup. Inner loops usam
        // a local, mantendo perf de const.
        int earlyExitDimPairs = s_earlyExitDimPairs;

        Span<Vector256<short>> qBroadcast = stackalloc Vector256<short>[dimPairs];
        for (int kp = 0; kp < dimPairs; kp++)
            qBroadcast[kp] = Vector256.Create(qPairs[kp]).AsInt16();

        Span<int> dists = stackalloc int[bv];

        // Dual-block loop: interleave loads for block b+1 with MADD
        // compute for block b, giving the OOE engine more independent
        // work and hiding L2/DRAM latency on cold clusters.
        int b = 0;
        for (; b + 1 < numBlocks; b += 2)
        {
            // Single base pointer + constant displacement (blockShorts) for
            // block b+1 instead of a second live local. ILC was reloading
            // blockPtr1 from the stack on every kp-iteration — folding the
            // disp into [base + idx*scale + disp32] frees that register and
            // kills the per-iter spill load (verified in objdump of v8).
            short* blockPtr0 = clusterVecBase + b * blockShorts;

            if (b + 4 < numBlocks)
                Sse.Prefetch0(clusterVecBase + (b + 4) * blockShorts);

            int validLanes0 = count - b * bv;
            if (validLanes0 > bv) validLanes0 = bv;
            int validLanes1 = count - (b + 1) * bv;
            if (validLanes1 > bv) validLanes1 = bv;

            Vector256<int> partial0 = Vector256<int>.Zero;
            Vector256<int> partial1 = Vector256<int>.Zero;
            for (int kp = 0; kp < earlyExitDimPairs; kp++)
            {
                var v0 = Avx2.LoadVector256(blockPtr0 + kp * 16).AsInt16();
                var v1 = Avx2.LoadVector256(blockPtr0 + blockShorts + kp * 16).AsInt16();
                var diff0 = Avx2.Subtract(v0, qBroadcast[kp]);
                var diff1 = Avx2.Subtract(v1, qBroadcast[kp]);
                partial0 = Avx2.Add(partial0, Avx2.MultiplyAddAdjacent(diff0, diff0));
                partial1 = Avx2.Add(partial1, Avx2.MultiplyAddAdjacent(diff1, diff1));
            }

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
                for (int kp = earlyExitDimPairs; kp < dimPairs; kp++)
                {
                    var v0 = Avx2.LoadVector256(blockPtr0 + kp * 16).AsInt16();
                    var diff0 = Avx2.Subtract(v0, qBroadcast[kp]);
                    full0 = Avx2.Add(full0, Avx2.MultiplyAddAdjacent(diff0, diff0));
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
                for (int kp = earlyExitDimPairs; kp < dimPairs; kp++)
                {
                    var v1 = Avx2.LoadVector256(blockPtr0 + blockShorts + kp * 16).AsInt16();
                    var diff1 = Avx2.Subtract(v1, qBroadcast[kp]);
                    full1 = Avx2.Add(full1, Avx2.MultiplyAddAdjacent(diff1, diff1));
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

        // Odd trailing block
        if (b < numBlocks)
        {
            short* blockPtr = clusterVecBase + b * blockShorts;
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

            if (passMask != 0)
            {
                Vector256<int> full = partial;
                for (int kp = earlyExitDimPairs; kp < dimPairs; kp++)
                {
                    var v = Avx2.LoadVector256(blockPtr + kp * 16).AsInt16();
                    var diff = Avx2.Subtract(v, qBroadcast[kp]);
                    full = Avx2.Add(full, Avx2.MultiplyAddAdjacent(diff, diff));
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
        if (_centroidsT != null)
            System.Runtime.InteropServices.NativeMemory.AlignedFree(_centroidsT);
        _exactAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
        _exactAccessor?.Dispose();
        _exactMmf?.Dispose();
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
    }

    private static short* AllocAndTransposeCentroids(short* centroids, int K, int pd)
    {
        nuint bytes = (nuint)((long)K * pd * sizeof(short));
        short* buf = (short*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(bytes, 64);
        for (int c = 0; c < K; c++)
            for (int d = 0; d < pd; d++)
                buf[(long)d * K + c] = centroids[(long)c * pd + d];
        return buf;
    }
}

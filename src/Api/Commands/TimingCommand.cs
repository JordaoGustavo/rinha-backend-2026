using System.Diagnostics;
using System.Text;

namespace Rinha.Api;

public static class TimingCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: Api timing <ivf.bin> [warmup.ndjson] [resources-dir] [count]");
            return 1;
        }

        string ivfPath = args[0];
        string warmupPath = args.Length > 1 ? args[1] : "resources/warmup-payloads.ndjson";
        string resourcesDir = args.Length > 2 ? args[2] : "resources";
        int count = args.Length > 3 ? int.Parse(args[3]) : 5000;

        TransactionParser.Initialize(
            Path.Combine(resourcesDir, "mcc_risk.json"),
            Path.Combine(resourcesDir, "normalization.json"));

        Console.WriteLine($"Opening IVF index from {ivfPath}...");
        using var detector = IvfDetector.Open(ivfPath);
        detector.Prefault();
        Console.WriteLine($"Loaded {detector.NumVectors} vectors, {detector.NumClusters} clusters");

        var payloads = LoadPayloads(warmupPath, count);
        Console.WriteLine($"Loaded {payloads.Count} payloads from {warmupPath}");
        if (payloads.Count == 0) { Console.Error.WriteLine("No payloads"); return 1; }

        // Warmup
        Console.WriteLine("Warming up (200 iters)...");
        Span<float> wvec = stackalloc float[16];
        for (int i = 0; i < 200; i++)
        {
            wvec.Clear();
            TransactionParser.Parse(payloads[i % payloads.Count], wvec);
            _ = detector.Score(wvec);
        }
        IvfDetector.ProfileFastPathLegitHits = 0;
        IvfDetector.ProfileFastPathFraudHits = 0;
        IvfDetector.ProfileFastPathMisses = 0;
        IvfDetector.RerankSkips = 0;

        double tickToUs = 1_000_000.0 / Stopwatch.Frequency;

        var parseUs = new double[count];
        var quantizeUs = new double[count];
        var profileUs = new double[count];
        var totalUs = new double[count];
        var centroidUs = new List<double>(count);
        var scanUs = new List<double>(count);
        var bboxUs = new List<double>(count);
        var rerankUs = new List<double>(count);
        var ivfTotalUs = new List<double>(count);
        var profileHit = new bool[count];

        Console.WriteLine($"Running {count} queries...");
        unsafe
        {
            for (int i = 0; i < count; i++)
            {
                var json = payloads[i % payloads.Count];
                Span<float> vec = stackalloc float[16];
                vec.Clear();

                // Stage 1: Parse
                long t0 = Stopwatch.GetTimestamp();
                TransactionParser.Parse(json, vec);
                long t1 = Stopwatch.GetTimestamp();
                parseUs[i] = (t1 - t0) * tickToUs;

                // Stage 2: Quantize (float→int16)
                const int pd = IvfBinaryFormat.PaddedDims;
                const int scale = IvfBinaryFormat.Scale;
                short* qInt = stackalloc short[pd];
                long t2 = Stopwatch.GetTimestamp();
                for (int d = 0; d < pd; d++)
                {
                    float v = MathF.Round(vec[d] * scale);
                    if (v > short.MaxValue) qInt[d] = short.MaxValue;
                    else if (v < short.MinValue) qInt[d] = short.MinValue;
                    else qInt[d] = (short)v;
                }
                long t3 = Stopwatch.GetTimestamp();
                quantizeUs[i] = (t3 - t2) * tickToUs;

                // Stage 3: Profile check (just the key computation)
                long t4 = Stopwatch.GetTimestamp();
                int pkey = ProfileFastPath.Key(qInt);
                long t5 = Stopwatch.GetTimestamp();
                profileUs[i] = (t5 - t4) * tickToUs;

                // Stage 4: Full score with per-stage timings
                long tScoreStart = Stopwatch.GetTimestamp();
                long fpLegitBefore = IvfDetector.ProfileFastPathLegitHits;
                long fpFraudBefore = IvfDetector.ProfileFastPathFraudHits;

                var ticks = new long[IvfDetector.TimingsCount];
                fixed (long* p = ticks)
                {
                    _ = detector.ScoreWithTimings(vec, p);
                }
                long tScoreEnd = Stopwatch.GetTimestamp();
                totalUs[i] = (tScoreEnd - t0) * tickToUs;

                bool wasProfileHit = IvfDetector.ProfileFastPathLegitHits > fpLegitBefore
                                  || IvfDetector.ProfileFastPathFraudHits > fpFraudBefore;
                profileHit[i] = wasProfileHit;

                if (!wasProfileHit)
                {
                    centroidUs.Add(ticks[0] * tickToUs);
                    scanUs.Add(ticks[1] * tickToUs);
                    bboxUs.Add(ticks[2] * tickToUs);
                    rerankUs.Add(ticks[3] * tickToUs);
                    ivfTotalUs.Add((tScoreEnd - tScoreStart) * tickToUs);
                }
            }
        }

        int profileHits = profileHit.Count(h => h);
        int profileMisses = count - profileHits;

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine("              HOT PATH TIMING BREAKDOWN");
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine($"Total queries:       {count}");
        Console.WriteLine($"Profile fast-path:   {profileHits} hits ({100.0 * profileHits / count:F1}%), {profileMisses} misses");
        Console.WriteLine();

        PrintStats("Parse (Utf8JsonReader)", parseUs);
        PrintStats("Quantize (float→int16)", quantizeUs);
        PrintStats("Profile key compute", profileUs);
        Console.WriteLine();
        PrintStats("TOTAL (all queries)", totalUs);

        if (ivfTotalUs.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"──── IVF PATH ONLY ({ivfTotalUs.Count} queries) ────");
            PrintStats("  Centroid scan", centroidUs.ToArray());
            PrintStats("  Cluster scan (nprobe)", scanUs.ToArray());
            PrintStats("  Bbox repair pass-2", bboxUs.ToArray());
            PrintStats("  Rerank/unanimity", rerankUs.ToArray());
            PrintStats("  IVF total (score only)", ivfTotalUs.ToArray());

            var ivfParseUs = new double[profileMisses];
            int idx = 0;
            for (int i = 0; i < count && idx < profileMisses; i++)
                if (!profileHit[i]) ivfParseUs[idx++] = parseUs[i];
            PrintStats("  Parse + IVF total (e2e)", ivfParseUs.Zip(ivfTotalUs).Select(t => t.First + t.Second).ToArray());
        }

        Console.WriteLine();
        Console.WriteLine("──── BUDGET TO HIT 1ms p99 (from LB perspective) ────");
        Console.WriteLine("  LB overhead:      ~50-100 µs");
        Console.WriteLine("  Socket I/O:       ~50-100 µs");
        Console.WriteLine("  Parse:            see above");
        Console.WriteLine("  Score:            see above");
        Console.WriteLine("  Response write:   ~10-20 µs");
        Console.WriteLine("  BUDGET REMAINING: ~700-800 µs for Parse+Score");

        long fpL = IvfDetector.ProfileFastPathLegitHits;
        long fpF = IvfDetector.ProfileFastPathFraudHits;
        long fpM = IvfDetector.ProfileFastPathMisses;
        long rks = IvfDetector.RerankSkips;
        Console.WriteLine();
        Console.WriteLine($"Rerank skips: {rks} / {fpM} IVF runs ({(fpM > 0 ? 100.0 * rks / fpM : 0):F1}%)");

        // Separate profile coverage run (Score, not ScoreWithTimings, so profile path is active)
        Console.WriteLine();
        Console.WriteLine("──── PROFILE COVERAGE (using Score, not ScoreWithTimings) ────");
        IvfDetector.ProfileFastPathLegitHits = 0;
        IvfDetector.ProfileFastPathFraudHits = 0;
        IvfDetector.ProfileFastPathMisses = 0;
        IvfDetector.RerankSkips = 0;

        var profileHitTimes = new List<double>(count);
        var profileMissTimes = new List<double>(count);

        for (int i = 0; i < count; i++)
        {
            var json = payloads[i % payloads.Count];
            Span<float> vec2 = stackalloc float[16];
            vec2.Clear();
            TransactionParser.Parse(json, vec2);

            long fpLBefore = IvfDetector.ProfileFastPathLegitHits;
            long fpFBefore = IvfDetector.ProfileFastPathFraudHits;

            long ts = Stopwatch.GetTimestamp();
            _ = detector.Score(vec2);
            long te = Stopwatch.GetTimestamp();
            double us = (te - ts) * tickToUs;

            bool wasHit = IvfDetector.ProfileFastPathLegitHits > fpLBefore
                       || IvfDetector.ProfileFastPathFraudHits > fpFBefore;
            if (wasHit) profileHitTimes.Add(us);
            else profileMissTimes.Add(us);
        }

        long pL2 = IvfDetector.ProfileFastPathLegitHits;
        long pF2 = IvfDetector.ProfileFastPathFraudHits;
        long pM2 = IvfDetector.ProfileFastPathMisses;
        long rks2 = IvfDetector.RerankSkips;
        long total2 = pL2 + pF2 + pM2;
        Console.WriteLine($"  Profile hits:    {pL2 + pF2} / {total2} ({(total2 > 0 ? 100.0 * (pL2 + pF2) / total2 : 0):F1}%)");
        Console.WriteLine($"    Legit hits:    {pL2}");
        Console.WriteLine($"    Fraud hits:    {pF2}");
        Console.WriteLine($"    Misses:        {pM2}");
        Console.WriteLine($"    Rerank skips:  {rks2} / {pM2} ({(pM2 > 0 ? 100.0 * rks2 / pM2 : 0):F1}%)");

        if (profileHitTimes.Count > 0)
            PrintStats("  Score (profile hit)", profileHitTimes.ToArray());
        if (profileMissTimes.Count > 0)
            PrintStats("  Score (profile miss/IVF)", profileMissTimes.ToArray());

        return 0;
    }

    private static void PrintStats(string label, double[] values)
    {
        if (values.Length == 0) { Console.WriteLine($"  {label}: (no data)"); return; }
        Array.Sort(values);
        double avg = values.Average();
        double p50 = values[(int)(values.Length * 0.50)];
        double p90 = values[(int)(values.Length * 0.90)];
        double p95 = values[(int)(values.Length * 0.95)];
        double p99 = values[(int)(values.Length * 0.99)];
        double max = values[^1];
        Console.WriteLine($"  {label,-32} avg={avg,8:F2}µs  p50={p50,8:F2}µs  p90={p90,8:F2}µs  p95={p95,8:F2}µs  p99={p99,8:F2}µs  max={max,8:F2}µs");
    }

    private static List<byte[]> LoadPayloads(string path, int maxCount)
    {
        var payloads = new List<byte[]>(maxCount);
        if (!File.Exists(path)) return payloads;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 4) continue;
            payloads.Add(Encoding.UTF8.GetBytes(line));
            if (payloads.Count >= maxCount) break;
        }
        return payloads;
    }
}

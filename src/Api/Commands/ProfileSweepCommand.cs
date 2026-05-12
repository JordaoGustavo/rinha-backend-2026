using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Rinha.Api;

public static class ProfileSweepCommand
{
    public static unsafe int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: Api profile-sweep <ivf.bin> <test-data.json> [resources-dir]");
            return 1;
        }

        string ivfPath = args[0];
        string testDataPath = args[1];
        string resourcesDir = args.Length > 2 ? args[2] : "resources";

        TransactionParser.Initialize(
            Path.Combine(resourcesDir, "mcc_risk.json"),
            Path.Combine(resourcesDir, "normalization.json"));

        string exactPath = Path.ChangeExtension(ivfPath, ".exact.bin");
        if (!File.Exists(exactPath))
            exactPath = Path.Combine(Path.GetDirectoryName(ivfPath)!, "exact.bin");

        Console.WriteLine($"Opening IVF index from {ivfPath}...");
        using var detector = File.Exists(exactPath)
            ? IvfDetector.OpenWithExactRerank(ivfPath, exactPath)
            : IvfDetector.Open(ivfPath);
        detector.Prefault();

        Console.WriteLine($"Loading test data from {testDataPath}...");
        var (payloads, expectedApproved, expectedScores) = LoadTestData(testDataPath);
        Console.WriteLine($"Loaded {payloads.Count} test entries");

        // Warmup
        Span<float> wvec = stackalloc float[16];
        for (int i = 0; i < Math.Min(200, payloads.Count); i++)
        {
            wvec.Clear();
            TransactionParser.Parse(payloads[i], wvec);
            _ = detector.Score(wvec);
        }

        // Pre-compute all vectors and their profile keys
        const int pd = IvfBinaryFormat.PaddedDims;
        const int scale = IvfBinaryFormat.Scale;
        int n = payloads.Count;
        var vectors = new float[n * pd];
        var profileKeys = new int[n];
        var ivfResults = new (bool Approved, int FraudCount)[n];

        Console.WriteLine("Pre-computing vectors and profile keys...");
        for (int i = 0; i < n; i++)
        {
            Span<float> vec = vectors.AsSpan(i * pd, pd);
            vec.Clear();
            TransactionParser.Parse(payloads[i], vec);

            short* qInt = stackalloc short[pd];
            for (int d = 0; d < pd; d++)
            {
                float v = MathF.Round(vec[d] * scale);
                if (v > short.MaxValue) qInt[d] = short.MaxValue;
                else if (v < short.MinValue) qInt[d] = short.MinValue;
                else qInt[d] = (short)v;
            }
            profileKeys[i] = ProfileFastPath.Key(qInt);
        }

        // Run IVF scoring (no profile, via ScoreWithTimings to bypass profile)
        Console.WriteLine("Running IVF scoring for all queries (no profile fast-path)...");
        var scoreTimes = new double[n];
        double tickToUs = 1_000_000.0 / Stopwatch.Frequency;
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<float> vec = vectors.AsSpan(i * pd, pd);
            var ticks = new long[IvfDetector.TimingsCount];
            long t0 = Stopwatch.GetTimestamp();
            fixed (long* p = ticks)
            {
                ivfResults[i] = detector.ScoreWithTimings(vec, p);
            }
            scoreTimes[i] = (Stopwatch.GetTimestamp() - t0) * tickToUs;
        }

        // Verify IVF accuracy first
        int ivfCorrect = 0, ivfFP = 0, ivfFN = 0;
        for (int i = 0; i < n; i++)
        {
            bool expected = expectedApproved[i];
            bool got = ivfResults[i].Approved;
            if (got == expected) ivfCorrect++;
            else if (got && !expected) ivfFN++;
            else ivfFP++;
        }
        Console.WriteLine();
        Console.WriteLine($"IVF baseline accuracy: {ivfCorrect}/{n} ({100.0 * ivfCorrect / n:F2}%)");
        Console.WriteLine($"  FP: {ivfFP}, FN: {ivfFN}");

        // Print details for every mismatch
        if (ivfFP + ivfFN > 0)
        {
            Console.WriteLine();
            Console.WriteLine("──── MISMATCH DETAILS (IVF baseline, no profile) ────");
            for (int i = 0; i < n; i++)
            {
                bool expected = expectedApproved[i];
                bool got = ivfResults[i].Approved;
                if (got == expected) continue;
                string kind = (got && !expected) ? "FN" : "FP";
                Console.WriteLine($"  [{kind}] entry {i}: expected approved={expected} score={expectedScores[i]:F1}, " +
                                  $"got approved={got} fraudCount={ivfResults[i].FraudCount}");
                Console.WriteLine($"    vector: [{string.Join(", ", Enumerable.Range(0, 14).Select(d => vectors[i * pd + d].ToString("F4")))}]");
                Console.WriteLine($"    payload: {Encoding.UTF8.GetString(payloads[i]).Substring(0, Math.Min(300, payloads[i].Length))}");
            }
        }

        // Now sweep PROFILE_MIN_COUNT from 1 to 30
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine("        PROFILE_MIN_COUNT SWEEP (accuracy + coverage)");
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine($"{"MinCount",8} {"Coverage",10} {"Correct",10} {"FP",5} {"FN",5} {"Accuracy",10} {"AvgScore µs",12} {"P99Score µs",12}");

        // Access the profile data from the detector via reflection-free approach:
        // Re-read the profile data directly from the mmap
        var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(ivfPath, FileMode.Open, null, 0,
            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

        int numVectors = (int)*(uint*)(basePtr + 8);
        int numClusters = (int)*(uint*)(basePtr + 12);
        int totalSlots = (int)*(uint*)(basePtr + 36);

        byte* profileMask = basePtr + IvfBinaryFormat.ProfileMaskOffset(numClusters, totalSlots);
        ushort* profileCount = (ushort*)(basePtr + IvfBinaryFormat.ProfileCountOffset(numClusters, totalSlots));

        int[] minCounts = [1, 2, 3, 5, 7, 10, 12, 15, 20, 25, 30, 50];
        foreach (int minCount in minCounts)
        {
            int hits = 0, misses = 0;
            int correct = 0, fp = 0, fn = 0;
            var hitTimes = new List<double>();
            var missTimes = new List<double>();

            for (int i = 0; i < n; i++)
            {
                int pkey = profileKeys[i];
                byte pmask = profileMask[pkey];
                ushort pcount = profileCount[pkey];

                bool profileHit = false;
                bool profileApproved = false;
                int profileFraudCount = 0;

                if (pmask == IvfBinaryFormat.ProfileLegitMask && pcount >= minCount)
                {
                    profileHit = true;
                    profileApproved = true;
                    profileFraudCount = 0;
                }
                else if (pmask == IvfBinaryFormat.ProfileFraudMask && pcount >= minCount)
                {
                    profileHit = true;
                    profileApproved = false;
                    profileFraudCount = 5;
                }

                bool approved;
                if (profileHit)
                {
                    hits++;
                    approved = profileApproved;
                    hitTimes.Add(0.07); // ~70ns
                }
                else
                {
                    misses++;
                    approved = ivfResults[i].Approved;
                    missTimes.Add(scoreTimes[i]);
                }

                if (approved == expectedApproved[i]) correct++;
                else if (approved && !expectedApproved[i]) fn++;
                else fp++;
            }

            double coverage = 100.0 * hits / n;
            double accuracy = 100.0 * correct / n;

            double avgScore = 0, p99Score = 0;
            if (missTimes.Count > 0)
            {
                missTimes.Sort();
                avgScore = missTimes.Average();
                p99Score = missTimes[(int)(missTimes.Count * 0.99)];
            }

            string mark = (fp == 0 && fn == 0) ? " ✓" : " ✗";
            Console.WriteLine($"{minCount,8} {coverage,9:F1}% {correct,10} {fp,5} {fn,5} {accuracy,9:F2}%{mark} {avgScore,11:F2} {p99Score,11:F2}");
        }

        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        accessor.Dispose();
        mmf.Dispose();

        return 0;
    }

    private static (List<byte[]> Payloads, bool[] ExpectedApproved, float[] ExpectedScores)
        LoadTestData(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var entries = doc.RootElement.GetProperty("entries");
        int count = entries.GetArrayLength();

        var payloads = new List<byte[]>(count);
        var approved = new bool[count];
        var scores = new float[count];
        int i = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            var request = entry.GetProperty("request");
            payloads.Add(Encoding.UTF8.GetBytes(request.GetRawText()));
            approved[i] = entry.GetProperty("expected_approved").GetBoolean();
            scores[i] = (float)entry.GetProperty("expected_fraud_score").GetDouble();
            i++;
        }

        return (payloads, approved, scores);
    }
}

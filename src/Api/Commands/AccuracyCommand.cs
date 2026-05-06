using System.Diagnostics;

namespace Rinha.Api;

public static class AccuracyCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: Api accuracy <ivf.bin> <exact.bin> [count] [seed]");
            return 1;
        }

        string ivfPath   = args[0];
        string exactPath = args[1];
        int count        = args.Length > 2 ? int.Parse(args[2]) : 10_000;
        int seed         = args.Length > 3 ? int.Parse(args[3]) : 0xBADBEEF;

        Console.WriteLine($"Opening IVF index from {ivfPath}...");
        using var ivf = IFraudDetector.Open(ivfPath);
        ivf.Prefault();

        Console.WriteLine($"Opening EXACT oracle from {exactPath}...");
        using var oracle = IFraudDetector.Open(exactPath);
        oracle.Prefault();

        Console.WriteLine($"Running {count} synthetic queries (seed={seed})...");
        var rng = new Random(seed);

        int approvedAgree = 0;
        int approvedDisagree = 0;
        int fraudCountDelta = 0;
        int fnOnIvf = 0;
        int fpOnIvf = 0;
        var firstFailures = new List<string>(capacity: 50);

        var swIvf = new Stopwatch();
        var swOracle = new Stopwatch();
        long ivfTicks = 0;
        long oracleTicks = 0;

        Span<float> q = stackalloc float[16];

        for (int i = 0; i < count; i++)
        {
            q.Clear();
            for (int d = 0; d < 14; d++) q[d] = (float)rng.NextDouble();
            // 5 % of queries flag last_transaction=null (sentinel -1 in dim 5,6)
            if (rng.NextDouble() < 0.05) { q[5] = -1f; q[6] = -1f; }

            swIvf.Restart();
            var (ivfApproved, ivfCount) = ivf.Score(q);
            swIvf.Stop();
            ivfTicks += swIvf.ElapsedTicks;

            swOracle.Restart();
            var (orcApproved, orcCount) = oracle.Score(q);
            swOracle.Stop();
            oracleTicks += swOracle.ElapsedTicks;

            if (ivfApproved == orcApproved) approvedAgree++;
            else
            {
                approvedDisagree++;
                if      (orcApproved && !ivfApproved) fpOnIvf++;
                else if (!orcApproved && ivfApproved) fnOnIvf++;

                if (firstFailures.Count < 50)
                {
                    string vec = string.Join(",", q[..14].ToArray()
                        .Select(v => v.ToString("F4")));
                    firstFailures.Add(
                        $"#{i} ivf=(approved={ivfApproved},frauds={ivfCount}) " +
                        $"oracle=(approved={orcApproved},frauds={orcCount}) vec=[{vec}]");
                }
            }
            if (ivfCount != orcCount) fraudCountDelta++;
        }

        double ivfMs    = (double)ivfTicks    / Stopwatch.Frequency * 1000.0 / count;
        double oracleMs = (double)oracleTicks / Stopwatch.Frequency * 1000.0 / count;

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("           ACCURACY HARNESS RESULTS");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine($"Total queries:           {count}");
        Console.WriteLine($"approved agreement:      {approvedAgree} ({100.0 * approvedAgree / count:F2}%)");
        Console.WriteLine($"approved disagreement:   {approvedDisagree}");
        Console.WriteLine($"  → IVF false-positive:  {fpOnIvf}  (oracle approved, IVF denied)");
        Console.WriteLine($"  → IVF false-negative:  {fnOnIvf}  (oracle denied,   IVF approved)");
        Console.WriteLine($"fraudCount mismatches:   {fraudCountDelta} ({100.0 * fraudCountDelta / count:F2}%)");
        Console.WriteLine();
        Console.WriteLine($"ivf    avg latency:      {ivfMs:F3} ms");
        Console.WriteLine($"oracle avg latency:      {oracleMs:F3} ms");
        Console.WriteLine();
        if (firstFailures.Count > 0)
        {
            Console.WriteLine($"First {firstFailures.Count} disagreements:");
            foreach (var f in firstFailures) Console.WriteLine($"  {f}");
        }

        return approvedDisagree > 0 ? 1 : 0;
    }
}

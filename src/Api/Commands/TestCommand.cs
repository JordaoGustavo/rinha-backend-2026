using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Rinha.Api;

public static class TestCommand
{
    public static async Task<int> Run(string[] args)
    {
        string baseUrl = args.Length > 0 ? args[0] : "http://localhost:8080";
        string testDataPath = args.Length > 1 ? args[1] : "/tmp/test-data.json";

        Console.WriteLine($"Loading test data from {testDataPath}...");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(testDataPath));
        var root = doc.RootElement;

        var stats = root.GetProperty("stats");
        Console.WriteLine($"Test data: {stats.GetProperty("total").GetInt32()} entries, " +
            $"fraud={stats.GetProperty("fraud_count").GetInt32()}, legit={stats.GetProperty("legit_count").GetInt32()}, " +
            $"edge_cases={stats.GetProperty("edge_case_count").GetInt32()}");

        var entries = root.GetProperty("entries");
        int total = entries.GetArrayLength();

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int httpErrors = 0;
        int falsePositives = 0;
        int falseNegatives = 0;
        int correct = 0;
        int scoreMatches = 0;
        int scoreMismatches = 0;
        var failures = new List<string>();
        var sw = Stopwatch.StartNew();
        var latencies = new List<double>(total);

        int concurrency = 10;
        var semaphore = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>();

        int progress = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            var request = entry.GetProperty("request");
            bool expectedApproved = entry.GetProperty("expected_approved").GetBoolean();
            float expectedScore = entry.GetProperty("expected_fraud_score").GetSingle();
            string txId = request.GetProperty("id").GetString()!;

            var requestJson = request.GetRawText();

            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var reqSw = Stopwatch.StartNew();
                    var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
                    var response = await http.PostAsync("/fraud-score", content);
                    reqSw.Stop();

                    lock (latencies) latencies.Add(reqSw.Elapsed.TotalMilliseconds);

                    if (!response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref httpErrors);
                        lock (failures) failures.Add($"HTTP {(int)response.StatusCode} for {txId}");
                        return;
                    }

                    var body = await response.Content.ReadAsStringAsync();
                    using var respDoc = JsonDocument.Parse(body);
                    var respRoot = respDoc.RootElement;

                    if (!respRoot.TryGetProperty("approved", out var approvedProp) ||
                        !respRoot.TryGetProperty("fraud_score", out var scoreProp))
                    {
                        Interlocked.Increment(ref httpErrors);
                        lock (failures) failures.Add($"Missing fields for {txId}: {body}");
                        return;
                    }

                    bool actualApproved = approvedProp.GetBoolean();
                    float actualScore = scoreProp.GetSingle();

                    if (actualApproved == expectedApproved)
                    {
                        Interlocked.Increment(ref correct);
                    }
                    else if (actualApproved && !expectedApproved)
                    {
                        Interlocked.Increment(ref falseNegatives);
                        lock (failures)
                        {
                            if (failures.Count < 50)
                                failures.Add($"FN {txId}: got approved=true, expected=false (expected_score={expectedScore}, actual_score={actualScore})");
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref falsePositives);
                        lock (failures)
                        {
                            if (failures.Count < 50)
                                failures.Add($"FP {txId}: got approved=false, expected=true (expected_score={expectedScore}, actual_score={actualScore})");
                        }
                    }

                    if (Math.Abs(actualScore - expectedScore) < 0.01f)
                        Interlocked.Increment(ref scoreMatches);
                    else
                        Interlocked.Increment(ref scoreMismatches);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref httpErrors);
                    lock (failures)
                    {
                        if (failures.Count < 50)
                            failures.Add($"Exception for {txId}: {ex.Message}");
                    }
                }
                finally
                {
                    semaphore.Release();
                    int p = Interlocked.Increment(ref progress);
                    if (p % 5000 == 0)
                        Console.Write($"\r  {p}/{total} ({100.0 * p / total:F1}%)...");
                }
            }));
        }

        await Task.WhenAll(tasks);
        sw.Stop();
        Console.WriteLine();

        latencies.Sort();
        double p50 = latencies[(int)(latencies.Count * 0.50)];
        double p95 = latencies[(int)(latencies.Count * 0.95)];
        double p99 = latencies[(int)(latencies.Count * 0.99)];

        int totalErrors = falsePositives + falseNegatives + httpErrors;
        double failureRate = (double)totalErrors / total;
        int weightedErrors = 1 * falsePositives + 3 * falseNegatives + 5 * httpErrors;
        double epsilon = (double)weightedErrors / total;

        double scoreP99Ms = p99;
        double scoreLatency = scoreP99Ms > 2000 ? -3000 : 1000 * Math.Log10(1000.0 / Math.Max(scoreP99Ms, 1));
        double scoreDet = failureRate > 0.15
            ? -3000
            : 1000 * Math.Log10(1.0 / Math.Max(epsilon, 0.001)) - 300 * Math.Log10(1 + weightedErrors);

        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("           INTEGRATION TEST RESULTS");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine($"Total requests:      {total}");
        Console.WriteLine($"Duration:            {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Throughput:          {total / sw.Elapsed.TotalSeconds:F0} req/s");
        Console.WriteLine();
        Console.WriteLine("─── Latency ───");
        Console.WriteLine($"p50:                 {p50:F2} ms");
        Console.WriteLine($"p95:                 {p95:F2} ms");
        Console.WriteLine($"p99:                 {p99:F2} ms");
        Console.WriteLine();
        Console.WriteLine("─── Detection ───");
        Console.WriteLine($"Correct:             {correct} ({100.0 * correct / total:F2}%)");
        Console.WriteLine($"False Positives:     {falsePositives} (weight 1)");
        Console.WriteLine($"False Negatives:     {falseNegatives} (weight 3)");
        Console.WriteLine($"HTTP Errors:         {httpErrors} (weight 5)");
        Console.WriteLine($"Score matches:       {scoreMatches} ({100.0 * scoreMatches / total:F2}%)");
        Console.WriteLine($"Score mismatches:    {scoreMismatches}");
        Console.WriteLine();
        Console.WriteLine("─── Scoring ───");
        Console.WriteLine($"Failure rate:        {failureRate:P2} (hard floor at 15%)");
        Console.WriteLine($"Weighted errors (E): {weightedErrors}");
        Console.WriteLine($"Epsilon (E/N):       {epsilon:F6}");
        Console.WriteLine($"score_p99:           {scoreLatency:F0}");
        Console.WriteLine($"score_det:           {scoreDet:F0}");
        Console.WriteLine($"TOTAL SCORE:         {scoreLatency + scoreDet:F0}");
        Console.WriteLine("═══════════════════════════════════════════════════");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"─── First {Math.Min(failures.Count, 50)} failures ───");
            foreach (var f in failures.Take(50))
                Console.WriteLine($"  {f}");
        }

        if (httpErrors > 0)
        {
            Console.Error.WriteLine($"\nFAILED: {httpErrors} HTTP errors detected!");
            return 1;
        }

        if (totalErrors == 0)
        {
            Console.WriteLine("\n✓ PERFECT: Zero failures across all test payloads!");
            return 0;
        }

        Console.WriteLine($"\n{totalErrors} detection errors (not HTTP errors).");
        return totalErrors > (int)(total * 0.15) ? 2 : 0;
    }
}

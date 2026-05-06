using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using Rinha.Api;

namespace Benchmarks;

[MemoryDiagnoser(displayGenColumns: true)]
[HideColumns(Column.RatioSD)]
public class HotPathBenchmarks
{
    private const int NumVectors = 200;
    private const int NumClusters = 4;
    private const int PaddedDims = 16;

    private string _tmpDir = null!;
    private IFraudDetector _ivf = null!;
    private IFraudDetector _kmknn = null!;
    private IFraudDetector _brute = null!;

    private float[] _query = null!;
    private byte[] _jsonBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"rinha_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);

        var rng = new Random(42);
        var ivfResult = BuildSyntheticIndex(rng, NumVectors, NumClusters);

        var ivfPath = Path.Combine(_tmpDir, "ivf.bin");
        IvfBinaryWriter.Write(ivfPath, ivfResult, nprobeFull: 4, nprobeFast: 2);
        _ivf = IvfDetector.Open(ivfPath);
        _ivf.Prefault();

        var kmknnPath = Path.Combine(_tmpDir, "kmknn.bin");
        KmknnBinaryWriter.Write(kmknnPath, ivfResult);
        _kmknn = KmknnDetector.Open(kmknnPath);
        _kmknn.Prefault();

        var brutePath = Path.Combine(_tmpDir, "brute.bin");
        BruteForceBinaryWriter.Write(brutePath, ivfResult.Vectors, ivfResult.Labels, ivfResult.NumVectors);
        _brute = BruteForceDetector.Open(brutePath);
        _brute.Prefault();

        _query = new float[PaddedDims];
        for (int i = 0; i < 14; i++)
            _query[i] = (float)rng.NextDouble();

        InitializeParser();
        _jsonBytes = BuildSampleJson();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ivf.Dispose();
        _kmknn.Dispose();
        _brute.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    // ── Individual component benchmarks ──

    [Benchmark]
    public (bool, int) IvfScore() => _ivf.Score(_query);

    [Benchmark]
    public (bool, int) KmknnScore() => _kmknn.Score(_query);

    [Benchmark]
    public (bool, int) BruteForceScore() => _brute.Score(_query);

    [Benchmark]
    public void Parse()
    {
        Span<float> vec = stackalloc float[16];
        TransactionParser.Parse(_jsonBytes, vec);
    }

    // ── Full pipeline benchmarks: Parse → Score ──

    [Benchmark]
    public (bool, int) Pipeline_Ivf()
    {
        Span<float> vec = stackalloc float[16];
        TransactionParser.Parse(_jsonBytes, vec);
        return _ivf.Score(vec);
    }

    [Benchmark]
    public (bool, int) Pipeline_Kmknn()
    {
        Span<float> vec = stackalloc float[16];
        TransactionParser.Parse(_jsonBytes, vec);
        return _kmknn.Score(vec);
    }

    [Benchmark]
    public (bool, int) Pipeline_BruteForce()
    {
        Span<float> vec = stackalloc float[16];
        TransactionParser.Parse(_jsonBytes, vec);
        return _brute.Score(vec);
    }

    // ── Helpers ──

    private static IvfResult BuildSyntheticIndex(Random rng, int n, int k)
    {
        var centroids = new float[k * PaddedDims];
        for (int c = 0; c < k; c++)
            for (int d = 0; d < 14; d++)
                centroids[c * PaddedDims + d] = (float)rng.NextDouble();

        var vectors = new float[n * PaddedDims];
        var labels = new byte[n];
        var clusterOffsets = new int[k];
        var clusterCounts = new int[k];

        int perCluster = n / k;
        for (int c = 0; c < k; c++)
        {
            clusterOffsets[c] = c * perCluster;
            clusterCounts[c] = c < k - 1 ? perCluster : n - c * perCluster;
        }

        for (int i = 0; i < n; i++)
        {
            int cluster = i / perCluster;
            if (cluster >= k) cluster = k - 1;
            for (int d = 0; d < 14; d++)
                vectors[i * PaddedDims + d] = centroids[cluster * PaddedDims + d] + (float)(rng.NextDouble() * 0.1 - 0.05);
            labels[i] = (byte)(rng.Next(2));
        }

        return new IvfResult
        {
            Centroids = centroids,
            Vectors = vectors,
            Labels = labels,
            ClusterOffsets = clusterOffsets,
            ClusterCounts = clusterCounts,
            NumVectors = n,
            NumClusters = k
        };
    }

    private static void InitializeParser()
    {
        var tmpResources = Path.Combine(Path.GetTempPath(), $"rinha_bench_res_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpResources);

        File.WriteAllText(Path.Combine(tmpResources, "mcc_risk.json"), """{"5411":0.1,"5912":0.3,"7995":0.9}""");
        File.WriteAllText(Path.Combine(tmpResources, "normalization.json"), """
        {
            "max_amount": 10000.0,
            "max_installments": 12.0,
            "amount_vs_avg_ratio": 10.0,
            "max_minutes": 1440.0,
            "max_km": 1000.0,
            "max_tx_count_24h": 20.0,
            "max_merchant_avg_amount": 10000.0
        }
        """);

        TransactionParser.Initialize(
            Path.Combine(tmpResources, "mcc_risk.json"),
            Path.Combine(tmpResources, "normalization.json"));

        try { Directory.Delete(tmpResources, true); } catch { }
    }

    private static byte[] BuildSampleJson() => Encoding.UTF8.GetBytes("""
    {
        "transaction": {
            "amount": 150.50,
            "installments": 3,
            "requested_at": "2025-03-15T14:30:00Z"
        },
        "customer": {
            "avg_amount": 200.0,
            "tx_count_24h": 5,
            "known_merchants": ["merchant_abc", "merchant_xyz"]
        },
        "merchant": {
            "id": "merchant_abc",
            "mcc": 5411,
            "avg_amount": 180.0
        },
        "terminal": {
            "is_online": true,
            "card_present": false,
            "km_from_home": 15.5
        },
        "last_transaction": {
            "timestamp": "2025-03-15T12:00:00Z",
            "km_from_current": 5.2
        }
    }
    """);
}

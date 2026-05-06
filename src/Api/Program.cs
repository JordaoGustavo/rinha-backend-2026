using System.IO.Pipelines;
using Rinha.Api;

ThreadPool.SetMinThreads(8, 8);

if (args.Length > 0)
{
    return args[0] switch
    {
        "preprocess" => PreprocessCommand.Run(args[1..]),
        "accuracy"   => AccuracyCommand.Run(args[1..]),
        _ => throw new ArgumentException($"Unknown command: {args[0]}")
    };
}

var resourcesPath = Environment.GetEnvironmentVariable("RESOURCES_PATH") ?? "/resources";
TransactionParser.Initialize(
    Path.Combine(resourcesPath, "mcc_risk.json"),
    Path.Combine(resourcesPath, "normalization.json"));

var dataPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "/data/ivf.bin";
var exactPath = Environment.GetEnvironmentVariable("EXACT_PATH");
var port = Environment.GetEnvironmentVariable("API_PORT") ?? "8080";
var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");

Console.WriteLine($"Opening index from {dataPath}...");
IFraudDetector detector;
if (!string.IsNullOrEmpty(exactPath) && File.Exists(exactPath))
{
    Console.WriteLine($"  with exact float32 rerank from {exactPath}");
    detector = IvfDetector.OpenWithExactRerank(dataPath, exactPath);
}
else
{
    detector = IFraudDetector.Open(dataPath);
}
Console.WriteLine($"Loaded {detector.NumVectors} vectors, {detector.NumClusters} clusters — {detector.Description}");
Console.WriteLine("Prefaulting pages...");
detector.Prefault();

const int warmupIterations = 200;
Console.WriteLine($"Warming up KNN scoring ({warmupIterations} queries)...");
{
    Span<float> warmVec = stackalloc float[16];
    var rng = new Random(42);
    for (int i = 0; i < warmupIterations; i++)
    {
        warmVec.Clear();
        for (int d = 0; d < 14; d++)
            warmVec[d] = (float)rng.NextDouble();
        _ = detector.Score(warmVec);
    }
}
Console.WriteLine("Ready.");

var responses = ResponseCache.Build();

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;

    if (!string.IsNullOrEmpty(socketPath))
    {
        if (File.Exists(socketPath)) File.Delete(socketPath);
        options.ListenUnixSocket(socketPath);
    }
    else
    {
        options.ListenAnyIP(int.Parse(port));
    }
});

var app = builder.Build();

if (!string.IsNullOrEmpty(socketPath))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        File.SetUnixFileMode(socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
    });
}

app.Run(async (HttpContext ctx) =>
{
    var path = ctx.Request.Path;

    if (path == "/ready")
    {
        ctx.Response.StatusCode = 200;
        return;
    }

    if (ctx.Request.Method != "POST" || path != "/fraud-score")
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    var pipeReader = ctx.Request.BodyReader;
    ReadResult result;
    if (!pipeReader.TryRead(out result) || result.Buffer.Length < (ctx.Request.ContentLength ?? 1))
        result = await pipeReader.ReadAtLeastAsync((int)(ctx.Request.ContentLength ?? 512));

    var body = result.Buffer;
    try
    {
        var (approved, fraudCount) = ProcessRequest(detector, body.FirstSpan);
        var responseBody = responses.Get(approved, fraudCount);
        ctx.Response.ContentLength = responseBody.Length;
        ctx.Response.ContentType = "application/json";
        var writer = ctx.Response.BodyWriter;
        var dst = writer.GetSpan(responseBody.Length);
        responseBody.CopyTo(dst);
        writer.Advance(responseBody.Length);
    }
    catch
    {
        // FN (weight 3) is cheaper than HTTP 5xx (weight 5); never 5xx.
        var fallback = responses.Get(true, 0);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength = fallback.Length;
        ctx.Response.ContentType = "application/json";
        var writer = ctx.Response.BodyWriter;
        var dst = writer.GetSpan(fallback.Length);
        fallback.CopyTo(dst);
        writer.Advance(fallback.Length);
    }
    finally
    {
        pipeReader.AdvanceTo(body.End);
    }
});

await app.RunAsync();
return 0;

static (bool Approved, int FraudCount) ProcessRequest(IFraudDetector detector, ReadOnlySpan<byte> json)
{
    Span<float> vector = stackalloc float[16];
    vector.Clear();
    TransactionParser.Parse(json, vector);
    return detector.Score(vector);
}

public sealed class ResponseCache
{
    private readonly byte[][] _responses = new byte[12][];

    public static ResponseCache Build()
    {
        var cache = new ResponseCache();
        for (int fraudCount = 0; fraudCount <= 5; fraudCount++)
        {
            float fraudScore = fraudCount * 0.2f;
            string scoreStr = fraudScore.ToString("F1");
            cache._responses[fraudCount] = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"approved\":true,\"fraud_score\":{scoreStr}}}");
            cache._responses[6 + fraudCount] = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"approved\":false,\"fraud_score\":{scoreStr}}}");
        }
        return cache;
    }

    public byte[] Get(bool approved, int fraudCount)
    {
        if ((uint)fraudCount > 5) fraudCount = 0;
        return _responses[approved ? fraudCount : 6 + fraudCount];
    }
}

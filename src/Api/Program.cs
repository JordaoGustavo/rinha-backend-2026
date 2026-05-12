using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime;
using System.Text;
using Kestrel.Transport.IoUring;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Rinha.Api;

// Suppress Gen2 collections during request handling. With zero-alloc hot path
// (stackalloc vectors, pre-baked responses, Utf8JsonReader), Gen2 heap grows
// very slowly — SustainedLowLatency prevents 1–5 ms GC pauses in p99 tail.
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

// Under CFS quota (0.4 CPU), extra threadpool workers compete for the same
// timeslice causing cross-worker handoff that shows up as 1–3 ms tail.
// pedrosakuma/rinha-2026 uses 1+1; cap at 2+2 for safety margin.
ThreadPool.SetMinThreads(1, 1);
ThreadPool.SetMaxThreads(2, 2);

bool serverTimingEnabled = Environment.GetEnvironmentVariable("SERVER_TIMING") == "1";

if (args.Length > 0)
{
    return args[0] switch
    {
        "preprocess"  => PreprocessCommand.Run(args[1..]),
        "accuracy"    => AccuracyCommand.Run(args[1..]),
        "gen-warmup"  => GenWarmupCommand.Run(args[1..]),
        "timing"        => TimingCommand.Run(args[1..]),
        "profile-sweep" => ProfileSweepCommand.Run(args[1..]),
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

// Warmup com payloads reais (variados em amount/mcc/km/flags) em vez de
// vetores random uniformes. Calibra branch predictor/TLB/icache pra
// distribuição realista do tráfego do bench oficial. Fallback para o
// caminho random se o arquivo não estiver disponível.
const int warmupIterations = 200;
{
    var warmupPath = Path.Combine(resourcesPath, "warmup-payloads.ndjson");
    Span<float> warmVec = stackalloc float[16];
    int warmedReal = 0;
    if (File.Exists(warmupPath))
    {
        Console.WriteLine($"Warming up KNN scoring with real payloads from {warmupPath}...");
        foreach (var line in File.ReadLines(warmupPath))
        {
            if (warmedReal >= warmupIterations) break;
            if (line.Length < 4) continue;
            warmVec.Clear();
            try
            {
                TransactionParser.Parse(System.Text.Encoding.UTF8.GetBytes(line), warmVec);
                _ = detector.Score(warmVec);
                warmedReal++;
            }
            catch { /* skip malformed line */ }
        }
        Console.WriteLine($"  warmed up with {warmedReal} real payloads.");
    }
    if (warmedReal < warmupIterations)
    {
        // Fallback / complemento: vetores random pra fechar warmupIterations
        Console.WriteLine($"  filling remaining {warmupIterations - warmedReal} with random vectors");
        var rng = new Random(42);
        for (int i = warmedReal; i < warmupIterations; i++)
        {
            warmVec.Clear();
            for (int d = 0; d < 14; d++)
                warmVec[d] = (float)rng.NextDouble();
            _ = detector.Score(warmVec);
        }
    }
}
Console.WriteLine("Ready.");

// Bypass total do Kestrel quando CUSTOM_HTTP=1: SocketHttpServer raw HTTP/1.1
// sobre o mesmo Unix socket. Pré-construído (origin/perf/custom-http-server,
// commit 5c187ca). Compatível com sno-forevis (LB Rust+mio epoll, sem
// parsing HTTP — verificado: strings -i não acha "http"/"GET"/"POST" no
// binário /proxy v0.0.2).
bool customHttp = Environment.GetEnvironmentVariable("CUSTOM_HTTP") == "1";
if (customHttp && !string.IsNullOrEmpty(socketPath))
{
    bool fdPassing = Environment.GetEnvironmentVariable("FD_PASSING") == "1";
    Console.WriteLine($"Custom HTTP server (Kestrel bypass) on {socketPath}" + (fdPassing ? " + .ctrl (FD passing)" : ""));
    var customResponses = HttpResponseTable.Build();
    using var server = new SocketHttpServer(socketPath, detector, customResponses);
    await server.RunAsync();
    return 0;
}

var responses = ResponseCache.Build();

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();

if (Environment.GetEnvironmentVariable("IO_URING_ENABLED") == "1")
{
    builder.WebHost.UseIoUring(opts =>
    {
        opts.RingSize = int.TryParse(Environment.GetEnvironmentVariable("IO_URING_SIZE"), out var rs) && rs > 0 ? rs : 256;
        opts.MaxConnections = int.TryParse(Environment.GetEnvironmentVariable("IO_URING_MAX_CONN"), out var mc) && mc > 0 ? mc : 1024;
    });
    Console.WriteLine("io_uring transport enabled");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
    options.Limits.MaxRequestBodySize = 8 * 1024;
    options.Limits.MaxRequestHeadersTotalSize = 4 * 1024;
    options.Limits.MaxConcurrentUpgradedConnections = 0;
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);

    if (!string.IsNullOrEmpty(socketPath))
    {
        if (File.Exists(socketPath)) File.Delete(socketPath);
        options.ListenUnixSocket(socketPath, lo => lo.Protocols = HttpProtocols.Http1);
    }
    else
    {
        options.ListenAnyIP(int.Parse(port), lo => lo.Protocols = HttpProtocols.Http1);
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

double tickToUs = 1_000_000.0 / Stopwatch.Frequency;

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
    ReadResult result = default;
    long needed = ctx.Request.ContentLength ?? 1;
    bool haveBody = false;
    // Fast-path: small spin trying TryRead before falling into async wait.
    // The body almost always arrives within microseconds over a Unix-socket
    // splice from haproxy; the await would otherwise cost a context switch +
    // task continuation that shows up as ~1-3ms in the p99.
    SpinWait spinner = default;
    for (int attempt = 0; attempt < 8; attempt++)
    {
        if (pipeReader.TryRead(out result))
        {
            if (result.Buffer.Length >= needed) { haveBody = true; break; }
            // Partial read; release the buffer back so the next TryRead can resume.
            pipeReader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
        spinner.SpinOnce();
    }
    if (!haveBody)
        result = await pipeReader.ReadAtLeastAsync((int)(ctx.Request.ContentLength ?? 512));

    var body = result.Buffer;
    try
    {
        long tStart = serverTimingEnabled ? Stopwatch.GetTimestamp() : 0;
        bool approved;
        int fraudCount;
        long parseTicks = 0;
        long[]? scoreTicks = null;
        if (serverTimingEnabled && detector is IvfDetector ivf)
        {
            (approved, fraudCount, parseTicks, scoreTicks) = ProcessRequestTimed(ivf, body.FirstSpan);
        }
        else
        {
            (approved, fraudCount) = ProcessRequest(detector, body.FirstSpan);
        }
        var responseBody = responses.Get(approved, fraudCount);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength = responseBody.Length;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.Date = default;
        if (serverTimingEnabled && scoreTicks != null)
        {
            long total = Stopwatch.GetTimestamp() - tStart;
            var sb = new StringBuilder(160);
            sb.Append("parse;dur=").Append((parseTicks * tickToUs).ToString("F2"));
            sb.Append(", s1-cent;dur=").Append((scoreTicks[0] * tickToUs).ToString("F2"));
            sb.Append(", s1-scan;dur=").Append((scoreTicks[1] * tickToUs).ToString("F2"));
            sb.Append(", s1-bbox;dur=").Append((scoreTicks[2] * tickToUs).ToString("F2"));
            sb.Append(", s2-rerank;dur=").Append((scoreTicks[3] * tickToUs).ToString("F2"));
            sb.Append(", total;dur=").Append((total * tickToUs).ToString("F2"));
            ctx.Response.Headers["Server-Timing"] = sb.ToString();
        }
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

static unsafe (bool Approved, int FraudCount, long ParseTicks, long[] ScoreTicks)
    ProcessRequestTimed(IvfDetector detector, ReadOnlySpan<byte> json)
{
    Span<float> vector = stackalloc float[16];
    vector.Clear();
    long t0 = Stopwatch.GetTimestamp();
    TransactionParser.Parse(json, vector);
    long t1 = Stopwatch.GetTimestamp();
    var ticks = new long[IvfDetector.TimingsCount];
    fixed (long* p = ticks)
    {
        var (approved, fraudCount) = detector.ScoreWithTimings(vector, p);
        return (approved, fraudCount, t1 - t0, ticks);
    }
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

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Rinha.Api;

public static class PreprocessCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: Api preprocess <references.json.gz> <output.bin> [clusters] [kmeans_iter] [format] [nprobe]");
            Console.Error.WriteLine("  format: ivf (default) or kmknn");
            return 1;
        }

        string inputPath = args[0];
        string outputPath = args[1];
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Reading {inputPath}...");

        var vectors = new List<float[]>(3_200_000);
        var labels = new List<byte>(3_200_000);

        using (var fs = File.OpenRead(inputPath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var doc = JsonDocument.Parse(gz))
        {
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var vecArray = entry.GetProperty("vector");
                var vec = new float[14];
                int i = 0;
                foreach (var dim in vecArray.EnumerateArray())
                    vec[i++] = dim.GetSingle();
                labels.Add(entry.GetProperty("label").GetString() == "fraud" ? (byte)1 : (byte)0);
                vectors.Add(vec);
            }
        }

        Console.WriteLine($"Loaded {vectors.Count} vectors in {sw.Elapsed.TotalSeconds:F1}s.");

        int n = vectors.Count;
        string format = args.Length > 4 ? args[4] : "ivf";
        const int defaultClusters = 4096;
        int parsedClusters = args.Length > 2 ? int.Parse(args[2]) : 0;
        int numClusters = parsedClusters > 0 ? parsedClusters : defaultClusters;
        int kmeansIter = args.Length > 3 ? int.Parse(args[3]) : 20;
        int nprobe = args.Length > 5 ? int.Parse(args[5]) : 40;

        string? dir = Path.GetDirectoryName(outputPath);
        if (dir != null) Directory.CreateDirectory(dir);

        Console.WriteLine($"Building index: format={format}, clusters={numClusters}, iterations={kmeansIter}...");
        var ivf = IvfBuilder.Build(vectors.ToArray(), labels.ToArray(), numClusters, kmeansIter);
        Console.WriteLine($"Writing {outputPath}...");

        if (format == "kmknn")
            KmknnBinaryWriter.Write(outputPath, ivf);
        else
            IvfBinaryWriter.Write(outputPath, ivf, nprobe);

        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s. {ivf.NumVectors} vectors, {ivf.NumClusters} clusters, format={format}, file size: {new FileInfo(outputPath).Length:N0} bytes");
        return 0;
    }
}

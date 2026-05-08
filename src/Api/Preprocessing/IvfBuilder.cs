using System.Numerics;
using System.Runtime.InteropServices;

namespace Rinha.Api;

public sealed class IvfResult
{
    public required float[] Centroids;
    public required float[] Vectors;
    public required byte[] Labels;
    public required int[] ClusterOffsets;
    public required int[] ClusterCounts;
    // Maps slot index (in clustered order) → original global index (for exact.bin reranking)
    public required int[] OriginalIndices;
    public int NumVectors;
    public int NumClusters;
}

public static class IvfBuilder
{
    public static IvfResult Build(float[][] inputVectors, byte[] inputLabels, int numClusters, int kmeansIterations = 20)
    {
        int n = inputVectors.Length;
        const int pd = 16;

        var padded = new float[n * pd];
        for (int i = 0; i < n; i++)
        {
            float[] src = inputVectors[i];
            int destOffset = i * pd;
            for (int d = 0; d < 14; d++)
                padded[destOffset + d] = src[d];
        }

        var centroids = new float[numClusters * pd];
        for (int c = 0; c < numClusters; c++)
        {
            int srcIdx = (int)((long)c * n / numClusters);
            Array.Copy(padded, srcIdx * pd, centroids, c * pd, pd);
        }

        var assignments = new int[n];
        for (int iter = 0; iter < kmeansIterations; iter++)
        {
            Console.Write($"\r  K-means iteration {iter + 1}/{kmeansIterations}...");
            AssignVectorsParallel(padded, centroids, assignments, n, numClusters);
            RecomputeCentroids(padded, assignments, centroids, n, numClusters);
        }
        Console.WriteLine();

        AssignVectorsParallel(padded, centroids, assignments, n, numClusters);

        var clusterCounts = new int[numClusters];
        for (int i = 0; i < n; i++)
            clusterCounts[assignments[i]]++;

        var clusterOffsets = new int[numClusters];
        clusterOffsets[0] = 0;
        for (int c = 1; c < numClusters; c++)
            clusterOffsets[c] = clusterOffsets[c - 1] + clusterCounts[c - 1];

        var outVectors = new float[n * pd];
        var outLabels = new byte[n];
        var originalIndices = new int[n];
        var insertPos = new int[numClusters];

        for (int i = 0; i < n; i++)
        {
            int c = assignments[i];
            int destIdx = clusterOffsets[c] + insertPos[c];
            insertPos[c]++;
            Array.Copy(padded, i * pd, outVectors, destIdx * pd, pd);
            outLabels[destIdx] = inputLabels[i];
            originalIndices[destIdx] = i;
        }

        // Sort vectors within each cluster by L2 distance to centroid.
        // A/B com cluster sort desligado (Mac local k6-varied 30s, n=1):
        //   p99 s1-bbox  com sort = 842 µs   /  sem sort = 1170 µs (+328)
        //   p99 score    com sort = 1700 µs  /  sem sort = 2010 µs (+310)
        // Confirmado: vetores próximos ao centroide primeiro derruba o
        // early-exit threshold do partial-sum cedo, e o ganho na cabeça
        // do cluster supera o custo na cauda.
        for (int c = 0; c < numClusters; c++)
        {
            int off = clusterOffsets[c];
            int cnt = clusterCounts[c];
            if (cnt <= 1) continue;

            var order = new int[cnt];
            var distArr = new float[cnt];
            int cOff = c * pd;
            for (int i = 0; i < cnt; i++)
            {
                order[i] = i;
                int vOff = (off + i) * pd;
                float d = 0;
                for (int dim = 0; dim < 14; dim++)
                {
                    float diff = outVectors[vOff + dim] - centroids[cOff + dim];
                    d += diff * diff;
                }
                distArr[i] = d;
            }

            Array.Sort(distArr, order);

            var tmpVec = new float[cnt * pd];
            var tmpLbl = new byte[cnt];
            var tmpOrig = new int[cnt];
            for (int i = 0; i < cnt; i++)
            {
                int src = off + order[i];
                Array.Copy(outVectors, src * pd, tmpVec, i * pd, pd);
                tmpLbl[i] = outLabels[src];
                tmpOrig[i] = originalIndices[src];
            }
            Array.Copy(tmpVec, 0, outVectors, off * pd, cnt * pd);
            Array.Copy(tmpLbl, 0, outLabels, off, cnt);
            Array.Copy(tmpOrig, 0, originalIndices, off, cnt);
        }
        Console.WriteLine("  Sorted vectors by centroid distance within each cluster.");

        return new IvfResult
        {
            Centroids = centroids,
            Vectors = outVectors,
            Labels = outLabels,
            OriginalIndices = originalIndices,
            ClusterOffsets = clusterOffsets,
            ClusterCounts = clusterCounts,
            NumVectors = n,
            NumClusters = numClusters
        };
    }

    private static void AssignVectorsParallel(float[] flatVectors, float[] centroids, int[] assignments, int numVectors, int numClusters)
    {
        const int pd = 16;
        int vecLen = Vector<float>.Count;

        Parallel.For(0, numVectors, i =>
        {
            int vOffset = i * pd;
            float bestDist = float.MaxValue;
            int bestCluster = 0;

            for (int c = 0; c < numClusters; c++)
            {
                int cOffset = c * pd;
                float dist = 0;
                int d = 0;
                for (; d + vecLen <= pd; d += vecLen)
                {
                    var vv = new Vector<float>(flatVectors, vOffset + d);
                    var cv = new Vector<float>(centroids, cOffset + d);
                    var diff = vv - cv;
                    dist += Vector.Dot(diff, diff);
                }
                for (; d < pd; d++)
                {
                    float diff = flatVectors[vOffset + d] - centroids[cOffset + d];
                    dist += diff * diff;
                }

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestCluster = c;
                }
            }

            assignments[i] = bestCluster;
        });
    }

    private static void RecomputeCentroids(float[] flatVectors, int[] assignments, float[] centroids, int numVectors, int numClusters)
    {
        const int pd = 16;

        Array.Clear(centroids);
        var counts = new int[numClusters];

        for (int i = 0; i < numVectors; i++)
        {
            int c = assignments[i];
            counts[c]++;
            int cOffset = c * pd;
            int vOffset = i * pd;
            for (int d = 0; d < 14; d++)
                centroids[cOffset + d] += flatVectors[vOffset + d];
        }

        for (int c = 0; c < numClusters; c++)
        {
            if (counts[c] == 0) continue;
            float inv = 1.0f / counts[c];
            int offset = c * pd;
            for (int d = 0; d < 14; d++)
                centroids[offset + d] *= inv;
        }
    }
}

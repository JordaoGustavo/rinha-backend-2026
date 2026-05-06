using System.Runtime.CompilerServices;

namespace Rinha.Api;

public static class KmknnSearch
{
    public static unsafe int FindKNearest(KmknnIndex index, short* query, int k,
        Span<int> resultIdx, int topClusters, bool useBboxRepair)
    {
        int numClusters = index.NumClusters;
        int actualTop = Math.Min(topClusters, numClusters);

        int* topIdx = stackalloc int[actualTop];
        int* topDist = stackalloc int[actualTop];
        int topSize = 0;

        for (int c = 0; c < numClusters; c++)
        {
            int d = SimdDistance.Int16L2Squared(query, index.GetCentroidPtr(c));
            if (topSize < actualTop)
                HeapPush(topIdx, topDist, ref topSize, c, d);
            else if (d < topDist[0])
                HeapReplaceTop(topIdx, topDist, topSize, c, d);
        }

        int* heapIdx = stackalloc int[k];
        int* heapDist = stackalloc int[k];
        int heapSize = 0;

        for (int p = 0; p < topSize; p++)
            ScanCluster(index, query, topIdx[p], heapIdx, heapDist, ref heapSize, k);

        if (useBboxRepair && topSize < numClusters)
        {
            int worstDist = heapSize == k ? heapDist[0] : int.MaxValue;
            for (int c = 0; c < numClusters; c++)
            {
                bool scanned = false;
                for (int j = 0; j < topSize; j++)
                    if (topIdx[j] == c) { scanned = true; break; }
                if (scanned) continue;

                int lb = SimdDistance.Int16BboxLowerBound(query, index.GetBboxMinPtr(c), index.GetBboxMaxPtr(c));
                if (lb <= worstDist)
                {
                    ScanCluster(index, query, c, heapIdx, heapDist, ref heapSize, k);
                    if (heapSize == k) worstDist = heapDist[0];
                }
            }
        }

        int resultCount = heapSize;
        for (int i = heapSize - 1; i >= 0; i--)
        {
            resultIdx[i] = heapIdx[0];
            HeapPopKnn(heapIdx, heapDist, ref heapSize);
        }
        return resultCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ScanCluster(KmknnIndex index, short* query, int clusterId,
        int* heapIdx, int* heapDist, ref int heapSize, int k)
    {
        ref var meta = ref index.GetClusterMeta(clusterId);
        int offset = (int)meta.Offset;
        int count = (int)meta.Count;
        if (count == 0) return;

        short* vecBase = index.GetVectorPtr(offset);
        const int pd = KmknnBinaryFormat.PaddedDims;
        const int prefetchAhead = 8;

        for (int i = 0; i < count; i++)
        {
            if (i + prefetchAhead < count)
                SimdDistance.Prefetch(vecBase + (i + prefetchAhead) * pd);

            short* vec = vecBase + i * pd;

            if (heapSize == k)
            {
                int worst = heapDist[0];
                int partial = SimdDistance.Int16L2SquaredFirst8(query, vec);
                if (partial >= worst) continue;
                int full = partial + SimdDistance.Int16L2SquaredLast8(query, vec);
                if (full < worst)
                    HeapReplaceTopKnn(heapIdx, heapDist, k, offset + i, full);
            }
            else
            {
                int distSq = SimdDistance.Int16L2Squared(query, vec);
                HeapPushKnn(heapIdx, heapDist, ref heapSize, offset + i, distSq);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void HeapPush(int* idx, int* dist, ref int size, int vectorIndex, int d)
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
    private static unsafe void HeapReplaceTop(int* idx, int* dist, int size, int vectorIndex, int d)
    {
        idx[0] = vectorIndex;
        dist[0] = d;
        int i = 0;
        while (true)
        {
            int largest = i, left = 2 * i + 1, right = 2 * i + 2;
            if (left < size && dist[left] > dist[largest]) largest = left;
            if (right < size && dist[right] > dist[largest]) largest = right;
            if (largest == i) break;
            (idx[largest], idx[i]) = (idx[i], idx[largest]);
            (dist[largest], dist[i]) = (dist[i], dist[largest]);
            i = largest;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void HeapPushKnn(int* idx, int* dist, ref int size, int vectorIndex, int d)
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
    private static unsafe void HeapReplaceTopKnn(int* idx, int* dist, int size, int vectorIndex, int d)
    {
        idx[0] = vectorIndex;
        dist[0] = d;
        int i = 0;
        while (true)
        {
            int largest = i, left = 2 * i + 1, right = 2 * i + 2;
            if (left < size && dist[left] > dist[largest]) largest = left;
            if (right < size && dist[right] > dist[largest]) largest = right;
            if (largest == i) break;
            (idx[largest], idx[i]) = (idx[i], idx[largest]);
            (dist[largest], dist[i]) = (dist[i], dist[largest]);
            i = largest;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void HeapPopKnn(int* idx, int* dist, ref int size)
    {
        int last = --size;
        idx[0] = idx[last];
        dist[0] = dist[last];
        if (size <= 0) return;
        int i = 0;
        while (true)
        {
            int largest = i, left = 2 * i + 1, right = 2 * i + 2;
            if (left < size && dist[left] > dist[largest]) largest = left;
            if (right < size && dist[right] > dist[largest]) largest = right;
            if (largest == i) break;
            (idx[largest], idx[i]) = (idx[i], idx[largest]);
            (dist[largest], dist[i]) = (dist[i], dist[largest]);
            i = largest;
        }
    }
}

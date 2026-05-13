namespace Rinha.Api;

public static class IvfBinaryWriter
{
    public static void Write(string path, IvfResult ivf, int nprobeFull = 40, int nprobeFast = 5)
    {
        int k = ivf.NumClusters;
        int n = ivf.NumVectors;
        const int pd = IvfBinaryFormat.PaddedDims;
        const int bv = IvfBinaryFormat.BlockVectors;
        const int scale = IvfBinaryFormat.Scale;

        var paddedOffsets = new int[k];
        var counts = new int[k];
        int totalSlots = 0;
        for (int c = 0; c < k; c++)
        {
            paddedOffsets[c] = totalSlots;
            counts[c] = ivf.ClusterCounts[c];
            int blocks = (counts[c] + bv - 1) / bv;
            totalSlots += blocks * bv;
        }

        var int16Vectors = new short[(long)totalSlots * pd];
        var paddedLabels = new byte[totalSlots];
        var paddedOriginalIndices = new int[totalSlots]; // -1 for padding slots
        Array.Fill(paddedOriginalIndices, -1);
        for (int c = 0; c < k; c++)
        {
            int srcOff = ivf.ClusterOffsets[c];
            int dstOff = paddedOffsets[c];
            int count = counts[c];
            for (int i = 0; i < count; i++)
            {
                int sBase = (srcOff + i) * pd;
                int dBase = (dstOff + i) * pd;
                for (int d = 0; d < pd; d++)
                    int16Vectors[dBase + d] = ClampToShort(ivf.Vectors[sBase + d] * scale);
                paddedLabels[dstOff + i] = ivf.Labels[srcOff + i];
                paddedOriginalIndices[dstOff + i] = ivf.OriginalIndices[srcOff + i];
            }
        }

        // Quantize centroids early — both the bbox empty-cluster fallback and
        // the per-cluster radius computation need them in int16 space.
        var int16Centroids = new short[k * pd];
        for (int i = 0; i < k * pd; i++)
            int16Centroids[i] = ClampToShort(ivf.Centroids[i] * scale);

        var bboxMin = new short[k * pd];
        var bboxMax = new short[k * pd];
        // v8: per-cluster radius = ceil(sqrt(max int16-squared distance from
        // int16 centroid to any int16 vector in the cluster)). Kept in the
        // same units as Int16L2Squared so triangle-inequality skip in pass-2
        // can compare directly against the int heap-distance.
        var clusterRadius = new uint[k];
        for (int c = 0; c < k; c++)
        {
            int baseOff = c * pd;
            for (int d = 0; d < pd; d++)
            {
                bboxMin[baseOff + d] = short.MaxValue;
                bboxMax[baseOff + d] = short.MinValue;
            }

            int dstOff = paddedOffsets[c];
            int count = counts[c];
            int maxSq = 0;
            for (int i = 0; i < count; i++)
            {
                int vOff = (dstOff + i) * pd;
                int sq = 0;
                for (int d = 0; d < pd; d++)
                {
                    short v = int16Vectors[vOff + d];
                    if (v < bboxMin[baseOff + d]) bboxMin[baseOff + d] = v;
                    if (v > bboxMax[baseOff + d]) bboxMax[baseOff + d] = v;
                    int diff = v - int16Centroids[baseOff + d];
                    sq += diff * diff;
                }
                if (sq > maxSq) maxSq = sq;
            }
            // ceil(sqrt) — conservative upper bound preserves triangle-skip
            // safety (no false rejects).
            clusterRadius[c] = maxSq == 0 ? 0u : (uint)Math.Ceiling(Math.Sqrt(maxSq));

            if (count == 0)
                for (int d = 0; d < pd; d++)
                {
                    short cv = int16Centroids[baseOff + d];
                    bboxMin[baseOff + d] = cv;
                    bboxMax[baseOff + d] = cv;
                }
        }

        int totalBlocks = totalSlots / bv;
        var blockedVectors = new short[(long)totalSlots * pd];
        for (int b = 0; b < totalBlocks; b++)
        {
            int blockStart = b * bv;
            for (int kp = 0; kp < pd / 2; kp++)
            {
                for (int v = 0; v < bv; v++)
                {
                    int srcBase = (blockStart + v) * pd + 2 * kp;
                    int dstBase = b * pd * bv + kp * 2 * bv + v * 2;
                    blockedVectors[dstBase + 0] = int16Vectors[srcBase + 0];
                    blockedVectors[dstBase + 1] = int16Vectors[srcBase + 1];
                }
            }
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var bw = new BinaryWriter(fs);

        bw.Write(IvfBinaryFormat.Magic);
        bw.Write(IvfBinaryFormat.Version);
        bw.Write((uint)n);
        bw.Write((uint)k);
        bw.Write((uint)IvfBinaryFormat.Dims);
        bw.Write((uint)IvfBinaryFormat.PaddedDims);
        bw.Write((uint)nprobeFast);
        bw.Write((uint)nprobeFull);
        bw.Write((uint)scale);
        bw.Write((uint)totalSlots);
        bw.Write(new byte[IvfBinaryFormat.HeaderSize - 10 * 4]);

        // v7+: centroids stored as int16 (scale=4096), matching the
        // query-side quantization. Stage-1 centroid scan uses
        // SimdDistance.Int16L2Squared instead of float L2 — same
        // ranking precision with half the bandwidth.
        WriteShortArray(bw, int16Centroids, k * pd);
        WriteShortArray(bw, bboxMin, k * pd);
        WriteShortArray(bw, bboxMax, k * pd);
        // v8: per-cluster radius for triangle-inequality skip in pass-2.
        WriteUInt32Array(bw, clusterRadius, k);

        for (int c = 0; c < k; c++)
        {
            bw.Write((uint)paddedOffsets[c]);
            bw.Write((uint)counts[c]);
        }

        WriteShortArray(bw, blockedVectors, totalSlots * pd);
        bw.Write(paddedLabels, 0, totalSlots);
        WriteInt32Array(bw, paddedOriginalIndices, totalSlots);

        // v9: profile fast path. Reusa os int16 já quantizados (linear
        // layout, NÃO o blockedVectors que é AoSoA-8). Iteramos slots
        // padded mas ignoramos os de padding (paddedOriginalIndices == -1).
        Console.WriteLine("  Building profile fast path mask + count...");
        var (profileMask, profileCount) = BuildProfileFromInt16Vectors(
            int16Vectors, paddedLabels, paddedOriginalIndices, totalSlots);
        bw.Write(profileMask, 0, profileMask.Length);
        WriteUInt16Array(bw, profileCount, profileCount.Length);

        long pureLegit = 0, pureFraud = 0, mixed = 0, empty = 0;
        for (int i = 0; i < profileMask.Length; i++)
        {
            switch (profileMask[i])
            {
                case 0: empty++; break;
                case IvfBinaryFormat.ProfileLegitMask: pureLegit++; break;
                case IvfBinaryFormat.ProfileFraudMask: pureFraud++; break;
                default: mixed++; break;
            }
        }
        Console.WriteLine($"  Profile: pure-legit={pureLegit:N0}, pure-fraud={pureFraud:N0}, mixed={mixed:N0}, empty={empty:N0}");

        bw.Flush();
    }

    private static (byte[] mask, ushort[] count) BuildProfileFromInt16Vectors(
        short[] vectors, byte[] labels, int[] originalIndices, int totalSlots)
    {
        var mask = new byte[IvfBinaryFormat.ProfileKeyCount];
        var count = new ushort[IvfBinaryFormat.ProfileKeyCount];
        const int pd = IvfBinaryFormat.PaddedDims;

        for (int i = 0; i < totalSlots; i++)
        {
            // Skip padding slots (no real training point assigned).
            if (originalIndices[i] < 0) continue;
            ReadOnlySpan<short> v = new ReadOnlySpan<short>(vectors, i * pd, pd);
            int key = ProfileFastPath.Key(v);
            if (count[key] < ushort.MaxValue) count[key]++;
            mask[key] |= labels[i] == 1
                ? IvfBinaryFormat.ProfileFraudMask
                : IvfBinaryFormat.ProfileLegitMask;
        }

        return (mask, count);
    }

    private static short ClampToShort(float v)
    {
        v = MathF.Round(v);
        if (v > short.MaxValue) return short.MaxValue;
        if (v < short.MinValue) return short.MinValue;
        return (short)v;
    }

    private static void WriteFloatArray(BinaryWriter bw, float[] data, int count)
    {
        var bytes = new byte[count * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteShortArray(BinaryWriter bw, short[] data, int count)
    {
        var bytes = new byte[count * sizeof(short)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteInt32Array(BinaryWriter bw, int[] data, int count)
    {
        var bytes = new byte[(long)count * sizeof(int)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteUInt32Array(BinaryWriter bw, uint[] data, int count)
    {
        var bytes = new byte[(long)count * sizeof(uint)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteUInt16Array(BinaryWriter bw, ushort[] data, int count)
    {
        var bytes = new byte[(long)count * sizeof(ushort)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bw.Write(bytes);
    }
}

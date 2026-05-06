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

        // Pad each cluster to a multiple of BlockVectors. Padding slots will be zeroed
        // (vector and label both zero). Padding vectors have distance 0 to a zero query
        // but are guarded against in the scan loop via `validLanes < 8`.
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

        // Quantize all vectors into AoS-padded layout (zero-init for padding slots).
        var int16Vectors = new short[(long)totalSlots * pd];
        var paddedLabels = new byte[totalSlots];
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
            }
        }

        // Compute int16 bboxes from valid (non-padding) vectors only.
        var bboxMin = new short[k * pd];
        var bboxMax = new short[k * pd];
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
            for (int i = 0; i < count; i++)
            {
                int vOff = (dstOff + i) * pd;
                for (int d = 0; d < pd; d++)
                {
                    short v = int16Vectors[vOff + d];
                    if (v < bboxMin[baseOff + d]) bboxMin[baseOff + d] = v;
                    if (v > bboxMax[baseOff + d]) bboxMax[baseOff + d] = v;
                }
            }

            if (count == 0)
                for (int d = 0; d < pd; d++)
                {
                    short cv = ClampToShort(ivf.Centroids[baseOff + d] * scale);
                    bboxMin[baseOff + d] = cv;
                    bboxMax[baseOff + d] = cv;
                }
        }

        // Transform AoS (already dim-permuted) → AoSoA-block layout for SIMD batched distance.
        // Per block, dims are stored as 8 dim-pairs (kp = 0..7), each as 2-int16 × bv lanes.
        // After permutation, dim-pair kp at storage holds INPUT dims (perm[2kp], perm[2kp+1]).
        // Phase boundaries fall on dim-pair boundaries:
        //   Phase 1: pairs 0,1   → input dims (5,6)(2,0)
        //   Phase 2: pairs 2,3   → input dims (7,8)(11,12)
        //   Phase 3: pairs 4,5   → input dims (9,10)(1,13)
        //   Phase 4: pairs 6,7   → input dims (3,4)(_,_)
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

        WriteFloatArray(bw, ivf.Centroids, k * pd);
        WriteShortArray(bw, bboxMin, k * pd);
        WriteShortArray(bw, bboxMax, k * pd);

        for (int c = 0; c < k; c++)
        {
            bw.Write((uint)paddedOffsets[c]);
            bw.Write((uint)counts[c]);
        }

        WriteShortArray(bw, blockedVectors, totalSlots * pd);
        bw.Write(paddedLabels, 0, totalSlots);
        bw.Flush();
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
}

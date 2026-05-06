namespace Rinha.Api;

public static class KmknnBinaryWriter
{
    public static void Write(string path, IvfResult ivf)
    {
        const int pd = KmknnBinaryFormat.PaddedDims;
        const int scale = KmknnBinaryFormat.Scale;
        int n = ivf.NumVectors;
        int k = ivf.NumClusters;

        var int16Centroids = new short[k * pd];
        for (int i = 0; i < k * pd; i++)
            int16Centroids[i] = (short)Math.Round(ivf.Centroids[i] * scale);

        var int16Vectors = new short[n * pd];
        for (int i = 0; i < n * pd; i++)
            int16Vectors[i] = (short)Math.Round(ivf.Vectors[i] * scale);

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

            int offset = ivf.ClusterOffsets[c];
            int count = ivf.ClusterCounts[c];

            for (int i = 0; i < count; i++)
            {
                int vOff = (offset + i) * pd;
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
                    bboxMin[baseOff + d] = int16Centroids[baseOff + d];
                    bboxMax[baseOff + d] = int16Centroids[baseOff + d];
                }
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var bw = new BinaryWriter(fs);

        bw.Write(KmknnBinaryFormat.Magic);
        bw.Write(KmknnBinaryFormat.Version);
        bw.Write((uint)n);
        bw.Write((uint)k);
        bw.Write((uint)KmknnBinaryFormat.Dims);
        bw.Write((uint)KmknnBinaryFormat.PaddedDims);
        bw.Write((uint)scale);
        bw.Write(new byte[KmknnBinaryFormat.HeaderSize - 7 * 4]);

        WriteShortArray(bw, int16Centroids, k * pd);
        WriteShortArray(bw, bboxMin, k * pd);
        WriteShortArray(bw, bboxMax, k * pd);

        for (int c = 0; c < k; c++)
        {
            bw.Write((uint)ivf.ClusterOffsets[c]);
            bw.Write((uint)ivf.ClusterCounts[c]);
        }

        WriteShortArray(bw, int16Vectors, n * pd);

        var labels = new byte[n];
        Array.Copy(ivf.Labels, labels, n);
        bw.Write(labels);
        bw.Flush();
    }

    private static void WriteShortArray(BinaryWriter bw, short[] data, int count)
    {
        var bytes = new byte[count * sizeof(short)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bw.Write(bytes);
    }
}

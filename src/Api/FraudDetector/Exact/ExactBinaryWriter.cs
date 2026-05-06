namespace Rinha.Api;

public static class ExactBinaryWriter
{
    public static void Write(string path, float[] paddedVectors, byte[] labels, int numVectors)
    {
        const int pd = ExactBinaryFormat.PaddedDims;
        if (paddedVectors.Length < (long)numVectors * pd)
            throw new ArgumentException($"paddedVectors too small: {paddedVectors.Length} < {(long)numVectors * pd}");
        if (labels.Length < numVectors)
            throw new ArgumentException($"labels too small: {labels.Length} < {numVectors}");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var bw = new BinaryWriter(fs);

        bw.Write(ExactBinaryFormat.Magic);
        bw.Write(ExactBinaryFormat.Version);
        bw.Write((uint)numVectors);
        bw.Write(new byte[ExactBinaryFormat.HeaderSize - 3 * 4]);

        var bytes = new byte[(long)numVectors * pd * sizeof(float)];
        Buffer.BlockCopy(paddedVectors, 0, bytes, 0, bytes.Length);
        bw.Write(bytes);

        bw.Write(labels, 0, numVectors);
        bw.Flush();
    }
}

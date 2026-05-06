namespace Rinha.Api;

public static class ExactBinaryFormat
{
    public static ReadOnlySpan<byte> Magic => "EXCT"u8;
    public const uint Version = 1;
    public const int HeaderSize = 32;
    public const int Dims = 14;
    public const int PaddedDims = 16;
    public const int VectorBytes = PaddedDims * sizeof(float);

    public static long VectorsOffset => HeaderSize;

    public static long LabelsOffset(int numVectors) =>
        VectorsOffset + (long)numVectors * VectorBytes;

    public static long TotalSize(int numVectors) =>
        LabelsOffset(numVectors) + numVectors;
}

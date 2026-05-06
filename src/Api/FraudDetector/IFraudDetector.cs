namespace Rinha.Api;

public readonly record struct SearchResult(int NodeIndex, double Distance);

public interface IFraudDetector : IDisposable
{
    int NumVectors { get; }
    int NumClusters { get; }
    string Description { get; }
    void Prefault();
    (bool Approved, int FraudCount) Score(ReadOnlySpan<float> query);

    static IFraudDetector Open(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        fs.ReadExactly(magic);

        if (magic.SequenceEqual("KMKN"u8))
            return KmknnDetector.Open(path);
        if (magic.SequenceEqual("IVFR"u8))
            return IvfDetector.Open(path);
        if (magic.SequenceEqual("EXCT"u8))
            return ExactDetector.Open(path);

        throw new InvalidDataException($"Unknown index format: {System.Text.Encoding.ASCII.GetString(magic)}");
    }
}

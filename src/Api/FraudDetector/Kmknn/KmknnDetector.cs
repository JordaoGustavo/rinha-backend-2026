namespace Rinha.Api;

public sealed class KmknnDetector : IFraudDetector
{
    private readonly KmknnIndex _index;
    private readonly int _topClustersFast;
    private readonly int _topClustersFull;

    public int NumVectors => _index.NumVectors;
    public int NumClusters => _index.NumClusters;
    public string Description => $"kMkNN {NumClusters} clusters, int16, top={_topClustersFast}/{_topClustersFull} adaptive";

    private KmknnDetector(KmknnIndex index, int topClustersFast, int topClustersFull)
    {
        _index = index;
        _topClustersFast = topClustersFast;
        _topClustersFull = topClustersFull;
    }

    public static KmknnDetector Open(string path)
    {
        int envFast = int.TryParse(Environment.GetEnvironmentVariable("NPROBE_FAST"), out var nf) ? nf : 0;
        int envFull = int.TryParse(Environment.GetEnvironmentVariable("NPROBE_FULL"), out var nF) ? nF : 0;
        int topFast = envFast > 0 ? envFast : 5;
        int topFull = envFull > 0 ? envFull : 40;
        return new(KmknnIndex.Open(path), topFast, topFull);
    }

    public unsafe (bool Approved, int FraudCount) Score(ReadOnlySpan<float> query)
    {
        short* q = stackalloc short[KmknnBinaryFormat.PaddedDims];
        for (int d = 0; d < KmknnBinaryFormat.PaddedDims; d++)
            q[d] = (short)MathF.Round(query[d] * KmknnBinaryFormat.Scale);

        Span<int> idxs = stackalloc int[5];
        int count = KmknnSearch.FindKNearest(_index, q, 5, idxs, _topClustersFast, useBboxRepair: false);

        int fraudCount = 0;
        for (int i = 0; i < count; i++)
            fraudCount += _index.GetLabel(idxs[i]);

        if (fraudCount >= 2 && fraudCount <= 4)
        {
            count = KmknnSearch.FindKNearest(_index, q, 5, idxs, _topClustersFull, useBboxRepair: true);
            fraudCount = 0;
            for (int i = 0; i < count; i++)
                fraudCount += _index.GetLabel(idxs[i]);
        }

        return (fraudCount < 3, fraudCount);
    }

    public void Prefault() => _index.Prefault();
    public void Dispose() => _index.Dispose();
}

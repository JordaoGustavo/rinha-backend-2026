using System.Runtime.CompilerServices;

namespace Rinha.Api;

/// <summary>
/// Profile fast path. Extrai
/// um hash de 22 bits de um subconjunto das 14 dims do embedding e usa um
/// bucket precomputado pra responder queries cuja região do feature space
/// é monocromática (todos os training points têm o mesmo label) sem rodar
/// o IVF. Cobertura típica: ~70-90% das queries em produção do dataset
/// rinha-2026; queries borderline (regiões mistas) caem no IVF + rerank.
///
/// Bit layout (Scale=4096, queries int16-quantizadas):
///   [3:0]   Bucket16(qInt[2])             — amount/avg ratio   (4 bits)
///   [6:4]   Bucket8(qInt[7])              — kmFromHome         (3 bits)
///   [8:7]   Bucket4(qInt[8])              — txCount24h         (2 bits)
///   [10:9]  Bucket4(qInt[12])             — mccRisk            (2 bits)
///   [12:11] Bucket4(qInt[0])              — txAmount           (2 bits)
///   [13]    qInt[5] &lt; 0                  — has-last-tx flag   (1 bit)
///   [14]    qInt[9] &gt; 0                  — terminalIsOnline   (1 bit)
///   [15]    qInt[10] &gt; 0                 — terminalCardPresent(1 bit)
///   [16]    qInt[11] &gt; 0                 — isUnknownMerchant  (1 bit)
///   [18:17] Bucket4(qInt[6])              — kmFromCurrent      (2 bits)
///   [19]    qInt[1] &gt; 410                — installments &gt;0.1 (1 bit)
///   [21:20] Bucket4(qInt[13])             — merchantAvgAmount  (2 bits)
/// Total = 22 bits.
/// </summary>
public static class ProfileFastPath
{
    // 0.1 normalizado * Scale 4096 ≈ 410. Equivalente ao "vec[1] > 1000"
    // Threshold "tem mais de uma
    // parcela ~tipica" — sinal binário simples de fraude.
    private const int InstallmentsThresholdQuantized = 410;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int Key(short* qInt)
    {
        int key = 0;
        key |= Bucket16(qInt[2]);
        key |= Bucket8(qInt[7]) << 4;
        key |= Bucket4(qInt[8]) << 7;
        key |= Bucket4(qInt[12]) << 9;
        key |= Bucket4(qInt[0]) << 11;
        key |= (qInt[5] < 0 ? 1 : 0) << 13;
        key |= (qInt[9] > 0 ? 1 : 0) << 14;
        key |= (qInt[10] > 0 ? 1 : 0) << 15;
        key |= (qInt[11] > 0 ? 1 : 0) << 16;
        key |= Bucket4(qInt[6]) << 17;
        key |= (qInt[1] > InstallmentsThresholdQuantized ? 1 : 0) << 19;
        key |= Bucket4(qInt[13]) << 20;
        return key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Key(ReadOnlySpan<short> qInt)
    {
        int key = 0;
        key |= Bucket16(qInt[2]);
        key |= Bucket8(qInt[7]) << 4;
        key |= Bucket4(qInt[8]) << 7;
        key |= Bucket4(qInt[12]) << 9;
        key |= Bucket4(qInt[0]) << 11;
        key |= (qInt[5] < 0 ? 1 : 0) << 13;
        key |= (qInt[9] > 0 ? 1 : 0) << 14;
        key |= (qInt[10] > 0 ? 1 : 0) << 15;
        key |= (qInt[11] > 0 ? 1 : 0) << 16;
        key |= Bucket4(qInt[6]) << 17;
        key |= (qInt[1] > InstallmentsThresholdQuantized ? 1 : 0) << 19;
        key |= Bucket4(qInt[13]) << 20;
        return key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bucket4(short value)
    {
        if (value <= 0) return 0;
        int b = value * 4 / (IvfBinaryFormat.Scale + 1);
        return b > 3 ? 3 : b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bucket8(short value)
    {
        if (value <= 0) return 0;
        int b = value * 8 / (IvfBinaryFormat.Scale + 1);
        return b > 7 ? 7 : b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bucket16(short value)
    {
        if (value <= 0) return 0;
        int b = value * 16 / (IvfBinaryFormat.Scale + 1);
        return b > 15 ? 15 : b;
    }

    /// <summary>
    /// Builder offline. Itera os training points (já em layout int16
    /// quantizado, padded a 16 dims) e computa mask + count por bucket.
    /// mask = 0 (unobserved) | 1 (legit) | 2 (fraud) | 3 (mixed). count
    /// satura em ushort.MaxValue.
    /// </summary>
    public static (byte[] mask, ushort[] count) Build(short[] vectors, byte[] labels, int numVectors)
    {
        var mask = new byte[IvfBinaryFormat.ProfileKeyCount];
        var count = new ushort[IvfBinaryFormat.ProfileKeyCount];
        const int pd = IvfBinaryFormat.PaddedDims;

        for (int i = 0; i < numVectors; i++)
        {
            int off = i * pd;
            ReadOnlySpan<short> v = new ReadOnlySpan<short>(vectors, off, pd);
            int key = Key(v);
            if (count[key] < ushort.MaxValue) count[key]++;
            mask[key] |= labels[i] == 1 ? IvfBinaryFormat.ProfileFraudMask : IvfBinaryFormat.ProfileLegitMask;
        }

        return (mask, count);
    }
}

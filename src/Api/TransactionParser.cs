using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Rinha.Api;

public static class TransactionParser
{
    private const int MccTableSize = 10000;
    private static readonly float[] _mccRisk = new float[MccTableSize];
    private const float _defaultMccRisk = 0.50f;
    private static double _maxAmount = 10000.0;
    private static double _maxInstallments = 12.0;
    private static double _amountVsAvgRatio = 10.0;
    private static double _maxMinutes = 1440.0;
    private static double _maxKm = 1000.0;
    private static double _maxTxCount24h = 20.0;
    private static double _maxMerchantAvgAmount = 10000.0;

    public static void Initialize(string mccRiskPath, string normalizationPath)
    {
        Array.Fill(_mccRisk, _defaultMccRisk);
        using var mccDoc = JsonDocument.Parse(File.ReadAllBytes(mccRiskPath));
        foreach (var prop in mccDoc.RootElement.EnumerateObject())
        {
            int mcc = int.Parse(prop.Name);
            if ((uint)mcc < MccTableSize)
                _mccRisk[mcc] = prop.Value.GetSingle();
        }

        using var normDoc = JsonDocument.Parse(File.ReadAllBytes(normalizationPath));
        var root = normDoc.RootElement;
        _maxAmount = root.GetProperty("max_amount").GetDouble();
        _maxInstallments = root.GetProperty("max_installments").GetDouble();
        _amountVsAvgRatio = root.GetProperty("amount_vs_avg_ratio").GetDouble();
        _maxMinutes = root.GetProperty("max_minutes").GetDouble();
        _maxKm = root.GetProperty("max_km").GetDouble();
        _maxTxCount24h = root.GetProperty("max_tx_count_24h").GetDouble();
        _maxMerchantAvgAmount = root.GetProperty("max_merchant_avg_amount").GetDouble();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GetMccRisk(int mcc) =>
        (uint)mcc < MccTableSize ? _mccRisk[mcc] : _defaultMccRisk;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ClampD(double x) => Math.Max(0.0, Math.Min(1.0, x));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FnvHash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ParseDoubleManual(ReadOnlySpan<byte> span, int start, out int end)
    {
        int p = start;
        bool neg = false;
        if (p < span.Length && span[p] == '-') { neg = true; p++; }
        long intPart = 0;
        while (p < span.Length && (uint)(span[p] - '0') <= 9)
        {
            intPart = intPart * 10 + (span[p] - '0');
            p++;
        }
        double result = intPart;
        if (p < span.Length && span[p] == '.')
        {
            p++;
            double frac = 0;
            double div = 10.0;
            while (p < span.Length && (uint)(span[p] - '0') <= 9)
            {
                frac += (span[p] - '0') / div;
                div *= 10.0;
                p++;
            }
            result += frac;
        }
        if (p < span.Length && (span[p] == 'e' || span[p] == 'E'))
        {
            p++;
            bool expNeg = false;
            if (p < span.Length && span[p] == '-') { expNeg = true; p++; }
            else if (p < span.Length && span[p] == '+') { p++; }
            int exp = 0;
            while (p < span.Length && (uint)(span[p] - '0') <= 9)
            {
                exp = exp * 10 + (span[p] - '0');
                p++;
            }
            result *= Math.Pow(10, expNeg ? -exp : exp);
        }
        end = p;
        return neg ? -result : result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ParseIntManual(ReadOnlySpan<byte> span, int start, out int end)
    {
        int p = start;
        int val = 0;
        while (p < span.Length && (uint)(span[p] - '0') <= 9)
        {
            val = val * 10 + (span[p] - '0');
            p++;
        }
        end = p;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindField(ReadOnlySpan<byte> json, int from, ReadOnlySpan<byte> fieldName)
    {
        int idx = json[from..].IndexOf(fieldName);
        if (idx < 0) return -1;
        return from + idx + fieldName.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipWhitespace(ReadOnlySpan<byte> json, int p)
    {
        while (p < json.Length && (json[p] == ' ' || json[p] == '\t' || json[p] == '\n' || json[p] == '\r'))
            p++;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FindAndParseBool(ReadOnlySpan<byte> json, ReadOnlySpan<byte> fieldName, int from)
    {
        int pos = FindField(json, from, fieldName);
        if (pos < 0) return false;
        pos = SkipWhitespace(json, pos);
        return pos < json.Length && json[pos] == 't';
    }

    public static void Parse(ReadOnlySpan<byte> json, Span<float> vector)
    {
        vector.Clear();

        // Transaction fields
        double txAmount = 0.0;
        int txInstallments = 0;
        int txHour = 0, txMinute = 0, txSecond = 0;
        int txYear = 0, txMonth = 0, txDay = 0;

        // Customer fields
        double custAvgAmount = 0.0;
        int custTxCount24h = 0;

        // Merchant fields
        ulong merchantIdHash = 0;
        bool hasMerchantId = false;
        int merchantMcc = -1;
        double merchantAvgAmount = 0.0;

        // Terminal fields
        bool terminalIsOnline = false;
        bool terminalCardPresent = false;
        double terminalKmFromHome = 0.0;

        // Last transaction fields
        bool hasLastTx = false;
        int lastYear = 0, lastMonth = 0, lastDay = 0, lastHour = 0, lastMinute = 0, lastSecond = 0;
        double lastTxKmFromCurrent = 0.0;

        // Known merchants
        Span<ulong> knownHashes = stackalloc ulong[32];
        int knownCount = 0;

        // --- transaction ---
        int txPos = FindField(json, 0, "\"transaction\""u8);
        if (txPos >= 0)
        {
            int amtPos = FindField(json, txPos, "\"amount\":"u8);
            if (amtPos >= 0)
            {
                amtPos = SkipWhitespace(json, amtPos);
                txAmount = ParseDoubleManual(json, amtPos, out _);
            }

            int instPos = FindField(json, txPos, "\"installments\":"u8);
            if (instPos >= 0)
            {
                instPos = SkipWhitespace(json, instPos);
                txInstallments = ParseIntManual(json, instPos, out _);
            }

            int ratPos = FindField(json, txPos, "\"requested_at\":"u8);
            if (ratPos >= 0)
            {
                ratPos = SkipWhitespace(json, ratPos);
                if (ratPos < json.Length && json[ratPos] == '"') ratPos++;
                if (ratPos + 19 <= json.Length)
                {
                    var ts = json.Slice(ratPos, 19);
                    txYear   = (ts[0]-'0')*1000 + (ts[1]-'0')*100 + (ts[2]-'0')*10 + (ts[3]-'0');
                    txMonth  = (ts[5]-'0')*10 + (ts[6]-'0');
                    txDay    = (ts[8]-'0')*10 + (ts[9]-'0');
                    txHour   = (ts[11]-'0')*10 + (ts[12]-'0');
                    txMinute = (ts[14]-'0')*10 + (ts[15]-'0');
                    txSecond = (ts[17]-'0')*10 + (ts[18]-'0');
                }
            }
        }

        // --- customer ---
        int custPos = FindField(json, 0, "\"customer\""u8);
        if (custPos >= 0)
        {
            int avgPos = FindField(json, custPos, "\"avg_amount\":"u8);
            if (avgPos >= 0)
            {
                avgPos = SkipWhitespace(json, avgPos);
                custAvgAmount = ParseDoubleManual(json, avgPos, out _);
            }

            int tcPos = FindField(json, custPos, "\"tx_count_24h\":"u8);
            if (tcPos >= 0)
            {
                tcPos = SkipWhitespace(json, tcPos);
                custTxCount24h = ParseIntManual(json, tcPos, out _);
            }

            // known_merchants array
            int kmPos = FindField(json, custPos, "\"known_merchants\":"u8);
            if (kmPos >= 0)
            {
                kmPos = SkipWhitespace(json, kmPos);
                if (kmPos < json.Length && json[kmPos] == '[')
                {
                    kmPos++;
                    while (kmPos < json.Length && knownCount < 32)
                    {
                        kmPos = SkipWhitespace(json, kmPos);
                        if (kmPos >= json.Length || json[kmPos] == ']') break;
                        if (json[kmPos] == ',') { kmPos++; continue; }
                        if (json[kmPos] == '"')
                        {
                            kmPos++;
                            int strEnd = json[kmPos..].IndexOf((byte)'"');
                            if (strEnd >= 0)
                            {
                                knownHashes[knownCount++] = FnvHash(json.Slice(kmPos, strEnd));
                                kmPos += strEnd + 1;
                            }
                            else break;
                        }
                        else break;
                    }
                }
            }
        }

        // --- merchant ---
        int mPos = FindField(json, 0, "\"merchant\""u8);
        if (mPos >= 0)
        {
            int idPos = FindField(json, mPos, "\"id\":"u8);
            if (idPos >= 0)
            {
                idPos = SkipWhitespace(json, idPos);
                if (idPos < json.Length && json[idPos] == '"')
                {
                    idPos++;
                    int strEnd = json[idPos..].IndexOf((byte)'"');
                    if (strEnd >= 0)
                    {
                        merchantIdHash = FnvHash(json.Slice(idPos, strEnd));
                        hasMerchantId = true;
                    }
                }
            }

            int mccPos = FindField(json, mPos, "\"mcc\":"u8);
            if (mccPos >= 0)
            {
                mccPos = SkipWhitespace(json, mccPos);
                if (mccPos < json.Length && json[mccPos] == '"')
                {
                    mccPos++;
                    merchantMcc = ParseIntManual(json, mccPos, out _);
                }
                else
                {
                    merchantMcc = ParseIntManual(json, mccPos, out _);
                }
            }

            // merchant avg_amount — search after mcc to avoid matching customer avg_amount
            int maPos = FindField(json, mPos, "\"avg_amount\":"u8);
            if (maPos >= 0)
            {
                maPos = SkipWhitespace(json, maPos);
                merchantAvgAmount = ParseDoubleManual(json, maPos, out _);
            }
        }

        // --- terminal ---
        int tPos = FindField(json, 0, "\"terminal\""u8);
        if (tPos >= 0)
        {
            terminalIsOnline = FindAndParseBool(json, "\"is_online\":"u8, tPos);
            terminalCardPresent = FindAndParseBool(json, "\"card_present\":"u8, tPos);

            int kmhPos = FindField(json, tPos, "\"km_from_home\":"u8);
            if (kmhPos >= 0)
            {
                kmhPos = SkipWhitespace(json, kmhPos);
                terminalKmFromHome = ParseDoubleManual(json, kmhPos, out _);
            }
        }

        // --- last_transaction ---
        int ltPos = FindField(json, 0, "\"last_transaction\""u8);
        if (ltPos >= 0)
        {
            ltPos = SkipWhitespace(json, ltPos);
            // Skip the colon
            if (ltPos < json.Length && json[ltPos] == ':') ltPos++;
            ltPos = SkipWhitespace(json, ltPos);

            if (ltPos < json.Length && json[ltPos] != 'n') // not null
            {
                hasLastTx = true;

                int tsPos = FindField(json, ltPos, "\"timestamp\":"u8);
                if (tsPos >= 0)
                {
                    tsPos = SkipWhitespace(json, tsPos);
                    if (tsPos < json.Length && json[tsPos] == '"') tsPos++;
                    if (tsPos + 19 <= json.Length)
                    {
                        var ts = json.Slice(tsPos, 19);
                        lastYear   = (ts[0]-'0')*1000 + (ts[1]-'0')*100 + (ts[2]-'0')*10 + (ts[3]-'0');
                        lastMonth  = (ts[5]-'0')*10 + (ts[6]-'0');
                        lastDay    = (ts[8]-'0')*10 + (ts[9]-'0');
                        lastHour   = (ts[11]-'0')*10 + (ts[12]-'0');
                        lastMinute = (ts[14]-'0')*10 + (ts[15]-'0');
                        lastSecond = (ts[17]-'0')*10 + (ts[18]-'0');
                    }
                }

                int kmcPos = FindField(json, ltPos, "\"km_from_current\":"u8);
                if (kmcPos >= 0)
                {
                    kmcPos = SkipWhitespace(json, kmcPos);
                    lastTxKmFromCurrent = ParseDoubleManual(json, kmcPos, out _);
                }
            }
        }

        // --- Build feature vector (same logic as original) ---

        // Day-of-week via Zeller's congruence
        int zZ, zC, zM;
        if (txMonth < 3) { zZ = (txYear - 1) % 100; zC = (txYear - 1) / 100; zM = txMonth + 12; }
        else             { zZ = txYear       % 100; zC = txYear       / 100; zM = txMonth;       }
        int zH = (txDay + 13 * (zM + 1) / 5 + zZ + zZ / 4 + zC / 4 + 5 * zC) % 7;
        int monBased = (zH + 5) % 7;

        vector[0] = (float)Math.Round(ClampD(txAmount / _maxAmount), 4);
        vector[1] = (float)Math.Round(ClampD(txInstallments / _maxInstallments), 4);
        vector[2] = custAvgAmount == 0.0
            ? 1.0f
            : (float)Math.Round(ClampD((txAmount / custAvgAmount) / _amountVsAvgRatio), 4);
        vector[3] = (float)Math.Round(txHour / 23.0, 4);
        vector[4] = (float)Math.Round(monBased / 6.0, 4);

        if (hasLastTx)
        {
            double minutes;
            if (txYear == lastYear && txMonth == lastMonth)
            {
                long deltaSec = ((long)(txDay    - lastDay))    * 86400L
                              + ((long)(txHour   - lastHour))   * 3600L
                              + ((long)(txMinute - lastMinute)) * 60L
                              +  (long)(txSecond - lastSecond);
                minutes = deltaSec / 60.0;
            }
            else
            {
                var a = new DateTime(txYear,   txMonth,   txDay,   txHour,   txMinute,   txSecond,   DateTimeKind.Utc);
                var b = new DateTime(lastYear, lastMonth, lastDay, lastHour, lastMinute, lastSecond, DateTimeKind.Utc);
                minutes = (a - b).TotalMinutes;
            }
            vector[5] = (float)Math.Round(ClampD(minutes / _maxMinutes), 4);
            vector[6] = (float)Math.Round(ClampD(lastTxKmFromCurrent / _maxKm), 4);
        }
        else
        {
            vector[5] = -1f;
            vector[6] = -1f;
        }

        vector[7] = (float)Math.Round(ClampD(terminalKmFromHome / _maxKm), 4);
        vector[8] = (float)Math.Round(ClampD(custTxCount24h / _maxTxCount24h), 4);
        vector[9] = terminalIsOnline ? 1f : 0f;
        vector[10] = terminalCardPresent ? 1f : 0f;

        bool isUnknown = true;
        if (hasMerchantId && knownCount > 0)
        {
            for (int i = 0; i < knownCount; i++)
            {
                if (knownHashes[i] == merchantIdHash) { isUnknown = false; break; }
            }
        }
        vector[11] = isUnknown ? 1f : 0f;

        vector[12] = GetMccRisk(merchantMcc);
        vector[13] = (float)Math.Round(ClampD(merchantAvgAmount / _maxMerchantAvgAmount), 4);
    }
}

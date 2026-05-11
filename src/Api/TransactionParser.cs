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
        using var mccDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(mccRiskPath));
        foreach (var prop in mccDoc.RootElement.EnumerateObject())
        {
            int mcc = int.Parse(prop.Name);
            if ((uint)mcc < MccTableSize)
                _mccRisk[mcc] = prop.Value.GetSingle();
        }

        using var normDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(normalizationPath));
        var root = normDoc.RootElement;
        _maxAmount = root.GetProperty("max_amount").GetDouble();
        _maxInstallments = root.GetProperty("max_installments").GetDouble();
        _amountVsAvgRatio = root.GetProperty("amount_vs_avg_ratio").GetDouble();
        _maxMinutes = root.GetProperty("max_minutes").GetDouble();
        _maxKm = root.GetProperty("max_km").GetDouble();
        _maxTxCount24h = root.GetProperty("max_tx_count_24h").GetDouble();
        _maxMerchantAvgAmount = root.GetProperty("max_merchant_avg_amount").GetDouble();
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float GetMccRisk(int mcc) =>
        (uint)mcc < MccTableSize ? _mccRisk[mcc] : _defaultMccRisk;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static double ClampD(double x) => Math.Max(0.0, Math.Min(1.0, x));

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
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

    public static void Parse(ReadOnlySpan<byte> json, Span<float> vector)
    {
        vector.Clear();

        double txAmount = 0.0;
        int txInstallments = 0;
        int txYear = 0, txMonth = 0, txDay = 0, txHour = 0, txMinute = 0, txSecond = 0;

        double custAvgAmount = 0.0;
        int custTxCount24h = 0;

        Span<ulong> knownHashes = stackalloc ulong[32];
        int knownCount = 0;
        ulong merchantIdHash = 0;
        bool hasMerchantId = false;

        int merchantMcc = -1;
        double merchantAvgAmount = 0.0;

        bool terminalIsOnline = false;
        bool terminalCardPresent = false;
        double terminalKmFromHome = 0.0;

        bool hasLastTx = false;
        int lastYear = 0, lastMonth = 0, lastDay = 0, lastHour = 0, lastMinute = 0, lastSecond = 0;
        double lastTxKmFromCurrent = 0.0;

        int ctx = 0;
        int fieldId = 0;
        bool inKnownMerchantsArray = false;
        int rootFieldId = 0;

        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    fieldId = 0;
                    if (ctx == 0)
                    {
                        if      (reader.ValueTextEquals("transaction"u8))     { fieldId = 10; rootFieldId = 10; }
                        else if (reader.ValueTextEquals("customer"u8))         { fieldId = 11; rootFieldId = 11; }
                        else if (reader.ValueTextEquals("merchant"u8))         { fieldId = 12; rootFieldId = 12; }
                        else if (reader.ValueTextEquals("terminal"u8))         { fieldId = 13; rootFieldId = 13; }
                        else if (reader.ValueTextEquals("last_transaction"u8)) { fieldId = 14; rootFieldId = 14; }
                    }
                    else if (ctx == 1)
                    {
                        if      (reader.ValueTextEquals("amount"u8))       fieldId = 1;
                        else if (reader.ValueTextEquals("installments"u8)) fieldId = 2;
                        else if (reader.ValueTextEquals("requested_at"u8)) fieldId = 3;
                    }
                    else if (ctx == 2)
                    {
                        if      (reader.ValueTextEquals("avg_amount"u8))      fieldId = 4;
                        else if (reader.ValueTextEquals("tx_count_24h"u8))    fieldId = 5;
                        else if (reader.ValueTextEquals("known_merchants"u8)) fieldId = 6;
                    }
                    else if (ctx == 3)
                    {
                        if      (reader.ValueTextEquals("id"u8))         fieldId = 7;
                        else if (reader.ValueTextEquals("mcc"u8))        fieldId = 8;
                        else if (reader.ValueTextEquals("avg_amount"u8)) fieldId = 9;
                    }
                    else if (ctx == 4)
                    {
                        if      (reader.ValueTextEquals("is_online"u8))    fieldId = 15;
                        else if (reader.ValueTextEquals("card_present"u8)) fieldId = 16;
                        else if (reader.ValueTextEquals("km_from_home"u8)) fieldId = 17;
                    }
                    else if (ctx == 5)
                    {
                        if      (reader.ValueTextEquals("timestamp"u8))      fieldId = 18;
                        else if (reader.ValueTextEquals("km_from_current"u8)) fieldId = 19;
                    }
                    break;

                case JsonTokenType.StartObject:
                    if (ctx == 0)
                    {
                        switch (fieldId)
                        {
                            case 10: ctx = 1; break;
                            case 11: ctx = 2; break;
                            case 12: ctx = 3; break;
                            case 13: ctx = 4; break;
                            case 14: ctx = 5; hasLastTx = true; break;
                        }
                        fieldId = 0;
                    }
                    break;

                case JsonTokenType.EndObject:
                    if (ctx != 0) ctx = 0;
                    break;

                case JsonTokenType.StartArray:
                    if (ctx == 2 && fieldId == 6)
                    {
                        inKnownMerchantsArray = true;
                        fieldId = 0;
                    }
                    break;

                case JsonTokenType.EndArray:
                    inKnownMerchantsArray = false;
                    break;

                case JsonTokenType.Null:
                    if (ctx == 0 && rootFieldId == 14)
                        hasLastTx = false;
                    break;

                case JsonTokenType.String:
                    if (inKnownMerchantsArray)
                    {
                        if (knownCount < 32)
                            knownHashes[knownCount++] = FnvHash(reader.ValueSpan);
                        break;
                    }
                    switch (ctx)
                    {
                        case 1:
                            if (fieldId == 3)
                            {
                                var span = reader.ValueSpan;
                                if (span.Length >= 19)
                                {
                                    txYear   = (span[0]-'0')*1000 + (span[1]-'0')*100 + (span[2]-'0')*10 + (span[3]-'0');
                                    txMonth  = (span[5]-'0')*10 + (span[6]-'0');
                                    txDay    = (span[8]-'0')*10 + (span[9]-'0');
                                    txHour   = (span[11]-'0')*10 + (span[12]-'0');
                                    txMinute = (span[14]-'0')*10 + (span[15]-'0');
                                    txSecond = (span[17]-'0')*10 + (span[18]-'0');
                                }
                            }
                            break;
                        case 3:
                            if (fieldId == 7)
                            {
                                merchantIdHash = FnvHash(reader.ValueSpan);
                                hasMerchantId = true;
                            }
                            else if (fieldId == 8)
                            {
                                if (int.TryParse(reader.ValueSpan, out int mccVal))
                                    merchantMcc = mccVal;
                            }
                            break;
                        case 5:
                            if (fieldId == 18)
                            {
                                var span = reader.ValueSpan;
                                if (span.Length >= 19)
                                {
                                    lastYear   = (span[0]-'0')*1000 + (span[1]-'0')*100 + (span[2]-'0')*10 + (span[3]-'0');
                                    lastMonth  = (span[5]-'0')*10 + (span[6]-'0');
                                    lastDay    = (span[8]-'0')*10 + (span[9]-'0');
                                    lastHour   = (span[11]-'0')*10 + (span[12]-'0');
                                    lastMinute = (span[14]-'0')*10 + (span[15]-'0');
                                    lastSecond = (span[17]-'0')*10 + (span[18]-'0');
                                }
                            }
                            break;
                    }
                    break;

                case JsonTokenType.Number:
                    switch (ctx)
                    {
                        case 1:
                            if (fieldId == 1)      txAmount = reader.GetDouble();
                            else if (fieldId == 2) txInstallments = reader.GetInt32();
                            break;
                        case 2:
                            if (fieldId == 4)      custAvgAmount = reader.GetDouble();
                            else if (fieldId == 5) custTxCount24h = reader.GetInt32();
                            break;
                        case 3:
                            if (fieldId == 9) merchantAvgAmount = reader.GetDouble();
                            break;
                        case 4:
                            if (fieldId == 17) terminalKmFromHome = reader.GetDouble();
                            break;
                        case 5:
                            if (fieldId == 19) lastTxKmFromCurrent = reader.GetDouble();
                            break;
                    }
                    break;

                case JsonTokenType.True:
                case JsonTokenType.False:
                    if (ctx == 4)
                    {
                        bool val = reader.TokenType == JsonTokenType.True;
                        if (fieldId == 15)      terminalIsOnline = val;
                        else if (fieldId == 16) terminalCardPresent = val;
                    }
                    break;
            }
        }

        // Day-of-week via Zeller's congruence (gregoriano) — evita
        // alocação de DateTime + lookup de DayOfWeek (~50–80 ns vs ~5 ns).
        // Equivalência verificada em TransactionParserTests para 2024..2027.
        int zZ, zC, zM;
        if (txMonth < 3) { zZ = (txYear - 1) % 100; zC = (txYear - 1) / 100; zM = txMonth + 12; }
        else             { zZ = txYear       % 100; zC = txYear       / 100; zM = txMonth;       }
        int zH = (txDay + 13 * (zM + 1) / 5 + zZ + zZ / 4 + zC / 4 + 5 * zC) % 7;
        // zH: 0=Sat, 1=Sun, 2=Mon, ..., 6=Fri. Mon-based 0..6 = (zH+5) % 7.
        int monBased = (zH + 5) % 7;

        vector[0] = (float)ClampD(txAmount / _maxAmount);
        vector[1] = (float)ClampD(txInstallments / _maxInstallments);
        vector[2] = custAvgAmount == 0.0
            ? 1.0f
            : (float)ClampD((txAmount / custAvgAmount) / _amountVsAvgRatio);
        vector[3] = (float)(txHour / 23.0);
        vector[4] = (float)(monBased / 6.0);

        if (hasLastTx)
        {
            // Aritmética escalar quando mesmo mês/ano (caso comum no rinha 2026
            // que tem last_tx geralmente <24h antes). Fallback para DateTime
            // só em cross-month/year.
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
            vector[5] = (float)ClampD(minutes / _maxMinutes);
            vector[6] = (float)ClampD(lastTxKmFromCurrent / _maxKm);
        }
        else
        {
            vector[5] = -1f;
            vector[6] = -1f;
        }

        vector[7] = (float)ClampD(terminalKmFromHome / _maxKm);
        vector[8] = (float)ClampD(custTxCount24h / _maxTxCount24h);
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
        vector[13] = (float)ClampD(merchantAvgAmount / _maxMerchantAvgAmount);
    }
}

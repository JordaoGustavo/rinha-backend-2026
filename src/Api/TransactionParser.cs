using System.Text.Json;

namespace Rinha.Api;

public static class TransactionParser
{
    // MCC codes are 4-digit ints (max 9999). Direct array indexing replaces
    // Dictionary<int, float> on the hot path: 1 branch + 1 load (~5ns) vs
    // hash + bucket walk + cache miss (~50-300ns).
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

    // FNV-1a 64-bit hash on UTF-8 bytes — zero allocation
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

        // --- raw fields -------------------------------------------------------
        double txAmount = 0.0;
        int txInstallments = 0;
        int txYear = 0, txMonth = 0, txDay = 0, txHour = 0, txMinute = 0, txSecond = 0;

        double custAvgAmount = 0.0;
        int custTxCount24h = 0;

        // Strategy 2: hash-based known_merchants — zero allocation
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

        // --- parser state -----------------------------------------------------
        //
        //  Context tracks which top-level object we are currently inside.
        //  0 = root, 1 = transaction, 2 = customer, 3 = merchant,
        //  4 = terminal, 5 = last_transaction
        int ctx = 0;

        // Strategy 1: field ID enum instead of string lastPropertyName
        // Field IDs per context:
        //   ctx=0 (root):        10=transaction, 11=customer, 12=merchant,
        //                         13=terminal, 14=last_transaction
        //   ctx=1 (transaction): 1=amount, 2=installments, 3=requested_at
        //   ctx=2 (customer):    4=avg_amount, 5=tx_count_24h, 6=known_merchants
        //   ctx=3 (merchant):    7=id, 8=mcc, 9=avg_amount
        //   ctx=4 (terminal):    15=is_online, 16=card_present, 17=km_from_home
        //   ctx=5 (last_tx):     18=timestamp, 19=km_from_current
        int fieldId = 0;
        bool inKnownMerchantsArray = false;

        // Track the previous root-level field for null detection
        int rootFieldId = 0;

        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    // Strategy 1: use ValueTextEquals with UTF-8 literals
                    fieldId = 0;
                    if (ctx == 0) // root
                    {
                        if      (reader.ValueTextEquals("transaction"u8))     { fieldId = 10; rootFieldId = 10; }
                        else if (reader.ValueTextEquals("customer"u8))         { fieldId = 11; rootFieldId = 11; }
                        else if (reader.ValueTextEquals("merchant"u8))         { fieldId = 12; rootFieldId = 12; }
                        else if (reader.ValueTextEquals("terminal"u8))         { fieldId = 13; rootFieldId = 13; }
                        else if (reader.ValueTextEquals("last_transaction"u8)) { fieldId = 14; rootFieldId = 14; }
                    }
                    else if (ctx == 1) // transaction
                    {
                        if      (reader.ValueTextEquals("amount"u8))       fieldId = 1;
                        else if (reader.ValueTextEquals("installments"u8)) fieldId = 2;
                        else if (reader.ValueTextEquals("requested_at"u8)) fieldId = 3;
                    }
                    else if (ctx == 2) // customer
                    {
                        if      (reader.ValueTextEquals("avg_amount"u8))      fieldId = 4;
                        else if (reader.ValueTextEquals("tx_count_24h"u8))    fieldId = 5;
                        else if (reader.ValueTextEquals("known_merchants"u8)) fieldId = 6;
                    }
                    else if (ctx == 3) // merchant
                    {
                        if      (reader.ValueTextEquals("id"u8))         fieldId = 7;
                        else if (reader.ValueTextEquals("mcc"u8))        fieldId = 8;
                        else if (reader.ValueTextEquals("avg_amount"u8)) fieldId = 9;
                    }
                    else if (ctx == 4) // terminal
                    {
                        if      (reader.ValueTextEquals("is_online"u8))    fieldId = 15;
                        else if (reader.ValueTextEquals("card_present"u8)) fieldId = 16;
                        else if (reader.ValueTextEquals("km_from_home"u8)) fieldId = 17;
                    }
                    else if (ctx == 5) // last_transaction
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
                    // last_transaction: null → hasLastTx stays false
                    if (ctx == 0 && rootFieldId == 14)
                        hasLastTx = false;
                    break;

                case JsonTokenType.String:
                    if (inKnownMerchantsArray)
                    {
                        // Strategy 2: store hash instead of string
                        if (knownCount < 32)
                            knownHashes[knownCount++] = FnvHash(reader.ValueSpan);
                        break;
                    }
                    switch (ctx)
                    {
                        case 1: // transaction
                            if (fieldId == 3) // requested_at
                            {
                                // Strategy 3: parse ISO 8601 directly from UTF-8 bytes
                                var span = reader.ValueSpan;
                                // Format: YYYY-MM-DDTHH:MM:SSZ  (20 bytes minimum)
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
                        case 3: // merchant
                            if (fieldId == 7) // id
                            {
                                // Strategy 2: hash instead of string
                                merchantIdHash = FnvHash(reader.ValueSpan);
                                hasMerchantId = true;
                            }
                            else if (fieldId == 8) // mcc (may arrive as string)
                            {
                                if (int.TryParse(reader.ValueSpan, out int mccVal))
                                    merchantMcc = mccVal;
                            }
                            break;
                        case 5: // last_transaction
                            if (fieldId == 18) // timestamp
                            {
                                // Strategy 3: parse ISO 8601 directly from UTF-8 bytes
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
                        case 1: // transaction
                            if (fieldId == 1)      txAmount = reader.GetDouble();
                            else if (fieldId == 2) txInstallments = reader.GetInt32();
                            break;
                        case 2: // customer
                            if (fieldId == 4)      custAvgAmount = reader.GetDouble();
                            else if (fieldId == 5) custTxCount24h = reader.GetInt32();
                            break;
                        case 3: // merchant
                            if (fieldId == 9) merchantAvgAmount = reader.GetDouble();
                            break;
                        case 4: // terminal
                            if (fieldId == 17) terminalKmFromHome = reader.GetDouble();
                            break;
                        case 5: // last_transaction
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

        // --- build DateTime values from parsed components ---------------------
        var txRequestedAt   = new DateTime(txYear, txMonth, txDay, txHour, txMinute, txSecond, DateTimeKind.Utc);
        var lastTxTimestamp = hasLastTx
            ? new DateTime(lastYear, lastMonth, lastDay, lastHour, lastMinute, lastSecond, DateTimeKind.Utc)
            : default;

        // --- normalize (double precision, round to 4 decimals like reference vectors) ---

        // [0] amount
        vector[0] = (float)Math.Round(ClampD(txAmount / _maxAmount), 4);

        // [1] installments
        vector[1] = (float)Math.Round(ClampD(txInstallments / _maxInstallments), 4);

        // [2] amount_vs_avg  (guard against div-by-zero → clamp to 1.0)
        vector[2] = custAvgAmount == 0.0
            ? 1.0f
            : (float)Math.Round(ClampD((txAmount / custAvgAmount) / _amountVsAvgRatio), 4);

        // [3] hour_of_day (UTC) — already UTC from parse
        vector[3] = (float)Math.Round(txHour / 23.0, 4);

        // [4] day_of_week  (mon=0, sun=6)
        // DotNet DayOfWeek: Sunday=0, Monday=1 ... Saturday=6
        DayOfWeek dow = txRequestedAt.DayOfWeek;
        int monBased = dow == DayOfWeek.Sunday ? 6 : (int)dow - 1;
        vector[4] = (float)Math.Round(monBased / 6.0, 4);

        // [5] minutes_since_last_tx
        // [6] km_from_last_tx
        if (hasLastTx)
        {
            double minutes = (txRequestedAt - lastTxTimestamp).TotalMinutes;
            vector[5] = (float)Math.Round(ClampD(minutes / _maxMinutes), 4);
            vector[6] = (float)Math.Round(ClampD(lastTxKmFromCurrent / _maxKm), 4);
        }
        else
        {
            vector[5] = -1f;
            vector[6] = -1f;
        }

        // [7] km_from_home
        vector[7] = (float)Math.Round(ClampD(terminalKmFromHome / _maxKm), 4);

        // [8] tx_count_24h
        vector[8] = (float)Math.Round(ClampD(custTxCount24h / _maxTxCount24h), 4);

        // [9] is_online
        vector[9] = terminalIsOnline ? 1f : 0f;

        // [10] card_present
        vector[10] = terminalCardPresent ? 1f : 0f;

        // [11] unknown_merchant — Strategy 2: hash comparison
        bool isUnknown = true;
        if (hasMerchantId && knownCount > 0)
        {
            for (int i = 0; i < knownCount; i++)
            {
                if (knownHashes[i] == merchantIdHash) { isUnknown = false; break; }
            }
        }
        vector[11] = isUnknown ? 1f : 0f;

        // [12] mcc_risk
        vector[12] = GetMccRisk(merchantMcc);

        // [13] merchant_avg_amount
        vector[13] = (float)Math.Round(ClampD(merchantAvgAmount / _maxMerchantAvgAmount), 4);

        // [14-15] padding (already cleared by vector.Clear())
    }
}

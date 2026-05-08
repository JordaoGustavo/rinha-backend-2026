using System.Globalization;
using System.Text;

namespace Rinha.Api;

/// <summary>
/// Gera um arquivo NDJSON com payloads de fraud-score variados pra
/// usar como warmup do detector. Substitui o warmup com `rng.NextDouble()`
/// uniforme, calibrando branch predictor / TLB / icache pra distribuição
/// realista do dataset.
///
/// Espelha a lógica de scripts/k6/bench-varied.js (cross-product de
/// amounts × mccs × km_from_home com módulo em flags/installments) mas
/// gera ~200 linhas em vez de 500.
/// </summary>
public static class GenWarmupCommand
{
    private static readonly double[] Amounts        = { 10, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 50000 };
    private static readonly int[]    Installments   = { 1, 3, 6, 12 };
    private static readonly int[]    Mccs           = { 5411, 5812, 5814, 5912, 5921, 5942, 6011, 7011, 7995, 4121 };
    private static readonly double[] KmFromHome     = { 0.2, 1.5, 5.0, 25.0, 200.0 };
    private static readonly int[]    TxCount24h     = { 0, 1, 3, 10, 50 };
    private static readonly bool[][] TerminalFlags  =
    {
        new[] { true,  true  },
        new[] { true,  false },
        new[] { false, true  },
        new[] { false, false },
    };
    private static readonly string[][] MerchantList =
    {
        Array.Empty<string>(),
        new[] { "m-001" },
        new[] { "m-001", "m-002" },
        new[] { "m-001", "m-002", "m-003", "m-004", "m-005" },
    };

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: Api gen-warmup <output.ndjson> [count=200]");
            return 1;
        }

        string outPath = args[0];
        int count      = args.Length > 1 ? int.Parse(args[1]) : 200;

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

        var ci = CultureInfo.InvariantCulture;
        // UTF-8 sem BOM — o leitor é JSON (Utf8JsonReader não engole BOM no início).
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var w = new StreamWriter(outPath, append: false, encoding);
        int i = 0, written = 0;
        foreach (var a in Amounts)
        {
            foreach (var m in Mccs)
            {
                foreach (var k in KmFromHome)
                {
                    if (written >= count) goto done;
                    int inst   = Installments[i % Installments.Length];
                    int tx24   = TxCount24h  [i % TxCount24h.Length];
                    var merch  = MerchantList[i % MerchantList.Length];
                    var flags  = TerminalFlags[i % TerminalFlags.Length];
                    i++;

                    var sb = new StringBuilder(512);
                    sb.Append("{\"id\":\"warm-tx-").Append(i.ToString(ci))
                      .Append("\",\"transaction\":{\"amount\":").Append(a.ToString("F2", ci))
                      .Append(",\"installments\":").Append(inst.ToString(ci))
                      .Append(",\"requested_at\":\"2026-04-15T13:42:11Z\"},\"customer\":{\"avg_amount\":")
                      .Append((a / 1.5).ToString("F2", ci))
                      .Append(",\"tx_count_24h\":").Append(tx24.ToString(ci))
                      .Append(",\"known_merchants\":[");
                    for (int mi = 0; mi < merch.Length; mi++)
                    {
                        if (mi > 0) sb.Append(',');
                        sb.Append('"').Append(merch[mi]).Append('"');
                    }
                    sb.Append("]},\"merchant\":{\"id\":\"m-001\",\"mcc\":\"")
                      .Append(m.ToString(ci)).Append("\",\"avg_amount\":")
                      .Append((a * 0.9).ToString("F2", ci))
                      .Append("},\"terminal\":{\"is_online\":")
                      .Append(flags[0] ? "true" : "false")
                      .Append(",\"card_present\":")
                      .Append(flags[1] ? "true" : "false")
                      .Append(",\"km_from_home\":").Append(k.ToString("F2", ci))
                      .Append("},\"last_transaction\":{\"timestamp\":\"2026-04-15T11:10:00Z\",\"km_from_current\":")
                      .Append((k / 2).ToString("F2", ci))
                      .Append("}}");

                    w.WriteLine(sb.ToString());
                    written++;
                }
            }
        }
        done:
        Console.WriteLine($"Wrote {written} warmup payloads to {outPath}");
        return 0;
    }
}

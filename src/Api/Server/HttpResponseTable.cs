using System.Text;

namespace Rinha.Api;

/// <summary>
/// Pre-built complete HTTP/1.1 response byte arrays — status line + minimal
/// headers + body — for every (approved × fraudCount) outcome plus /ready and
/// 404. Each Send() to the socket is exactly one of these buffers, so we
/// touch the response composition path zero times per request.
///
/// Frames are HTTP/1.1 keep-alive friendly (no Connection header) and omit
/// Date/Server to shave bytes the rinha k6 client doesn't validate.
/// </summary>
public sealed class HttpResponseTable
{
    private readonly byte[][] _outcomes = new byte[12][];
    public byte[] Ready { get; }
    public byte[] NotFound { get; }
    public byte[] BadRequest { get; }

    private HttpResponseTable()
    {
        Ready      = BuildEmpty(200, "OK");
        NotFound   = BuildEmpty(404, "Not Found");
        BadRequest = BuildEmpty(400, "Bad Request");
    }

    public static HttpResponseTable Build()
    {
        var table = new HttpResponseTable();
        for (int fraudCount = 0; fraudCount <= 5; fraudCount++)
        {
            float fraudScore = fraudCount * 0.2f;
            string scoreStr = fraudScore.ToString("F1");
            string approvedBody = $"{{\"approved\":true,\"fraud_score\":{scoreStr}}}";
            string deniedBody   = $"{{\"approved\":false,\"fraud_score\":{scoreStr}}}";
            table._outcomes[fraudCount]     = BuildFramed(200, "OK", approvedBody);
            table._outcomes[6 + fraudCount] = BuildFramed(200, "OK", deniedBody);
        }
        return table;
    }

    public byte[] Get(bool approved, int fraudCount)
    {
        if ((uint)fraudCount > 5) fraudCount = 0;
        return _outcomes[approved ? fraudCount : 6 + fraudCount];
    }

    private static byte[] BuildFramed(int status, string reason, string jsonBody)
    {
        byte[] body = Encoding.UTF8.GetBytes(jsonBody);
        string head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"Content-Type: application/json\r\n" +
            "\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        byte[] full = new byte[headBytes.Length + body.Length];
        Buffer.BlockCopy(headBytes, 0, full, 0, headBytes.Length);
        Buffer.BlockCopy(body, 0, full, headBytes.Length, body.Length);
        return full;
    }

    private static byte[] BuildEmpty(int status, string reason)
    {
        string head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Length: 0\r\n" +
            "\r\n";
        return Encoding.ASCII.GetBytes(head);
    }
}

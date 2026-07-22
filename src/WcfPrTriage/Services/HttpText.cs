using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Text;

namespace WcfPrTriage.Services;

/// <summary>Helpers for reading HTTP response bodies with a hard size ceiling.</summary>
internal static class HttpText
{
    /// <summary>Default ceiling for a single log/console download (16 MB).</summary>
    public const int DefaultMaxBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Reads the response content as UTF-8 text but stops after <paramref name="maxBytes"/>, so a
    /// pathologically large log can't blow up memory. If the body is truncated, a marker line is
    /// appended. Reads incrementally from a pooled buffer rather than materializing the whole body.
    /// </summary>
    public static async Task<string> ReadStringCappedAsync(
        HttpContent content, CancellationToken ct, int maxBytes = DefaultMaxBytes)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(Math.Min(maxBytes, 1 << 20));
        byte[] rented = ArrayPool<byte>.Shared.Rent(81920);
        bool truncated = false;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(rented, ct).ConfigureAwait(false)) > 0)
            {
                int remaining = maxBytes - (int)ms.Length;
                if (read >= remaining)
                {
                    ms.Write(rented, 0, remaining);
                    truncated = true;
                    break;
                }
                ms.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        int length = (int)ms.Length;
        byte[] buffer = ms.GetBuffer();

        // A byte-count cap can slice through a multi-byte UTF-8 sequence; back off to the last complete
        // one so the tail doesn't decode to a replacement char. (Only truncation can leave a partial
        // sequence — a fully read valid-UTF-8 body already ends on a boundary.)
        if (truncated)
            length = TrimToUtf8Boundary(buffer, length);

        string text = Encoding.UTF8.GetString(buffer, 0, length);
        return truncated ? text + "\n… [log truncated by WcfPrTriage — exceeded size cap] …" : text;
    }

    /// <summary>Returns a length ≤ <paramref name="length"/> that ends on a complete UTF-8 sequence,
    /// dropping a trailing partial (truncated) multi-byte character if present.</summary>
    private static int TrimToUtf8Boundary(byte[] buf, int length)
    {
        if (length == 0)
            return 0;

        int i = length - 1;
        int continuations = 0;
        while (i >= 0 && (buf[i] & 0xC0) == 0x80)   // 10xxxxxx continuation byte
        {
            i--;
            continuations++;
        }
        if (i < 0)
            return length;   // no lead byte found (malformed) — leave as-is

        byte lead = buf[i];
        int seqLen =
            lead < 0x80 ? 1 :
            (lead & 0xE0) == 0xC0 ? 2 :
            (lead & 0xF0) == 0xE0 ? 3 :
            (lead & 0xF8) == 0xF0 ? 4 : 1;

        // Complete sequence → keep everything; otherwise cut the partial trailing char.
        return continuations + 1 == seqLen ? length : i;
    }
}

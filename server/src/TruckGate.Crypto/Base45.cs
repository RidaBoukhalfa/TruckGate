using System.Text;

namespace TruckGate.Crypto;

/// <summary>Base45 (RFC 9285). Strict decoder: rejects unknown chars, bad length and overflowing chunks.</summary>
public static class Base45
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length / 2 * 3 + 2);
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
        {
            int n = data[i] * 256 + data[i + 1];
            sb.Append(Alphabet[n % 45]);
            n /= 45;
            sb.Append(Alphabet[n % 45]);
            sb.Append(Alphabet[n / 45]);
        }
        if (data.Length % 2 == 1)
        {
            int n = data[^1];
            sb.Append(Alphabet[n % 45]);
            sb.Append(Alphabet[n / 45]);
        }
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var vals = new int[text.Length];
        for (int k = 0; k < text.Length; k++)
        {
            int v = Alphabet.IndexOf(text[k], StringComparison.Ordinal);
            if (v < 0) throw new TicketFormatException("invalid base45 character");
            vals[k] = v;
        }
        if (vals.Length % 3 == 1) throw new TicketFormatException("invalid base45 length");

        var outBytes = new List<byte>(vals.Length / 3 * 2 + 1);
        int i = 0;
        for (; i + 2 < vals.Length; i += 3)
        {
            int n = vals[i] + vals[i + 1] * 45 + vals[i + 2] * 45 * 45;
            if (n > 0xFFFF) throw new TicketFormatException("base45 triplet overflow");
            outBytes.Add((byte)(n >> 8));
            outBytes.Add((byte)(n & 0xFF));
        }
        if (vals.Length % 3 == 2)
        {
            int n = vals[^2] + vals[^1] * 45;
            if (n > 0xFF) throw new TicketFormatException("base45 pair overflow");
            outBytes.Add((byte)n);
        }
        return outBytes.ToArray();
    }
}

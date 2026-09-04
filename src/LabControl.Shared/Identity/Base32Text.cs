using System.Text;

namespace LabControl.Shared.Identity;

/// <summary>
/// Crockford base32 for the strings a human retypes: the recovery code and enrollment
/// codes. No <c>I</c>, <c>L</c>, <c>O</c> or <c>U</c> in the alphabet, and reading maps
/// the look-alikes back, so a code copied off a printed sheet by hand still parses.
/// </summary>
public static class Base32Text
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);

        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
        {
            text.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return text.ToString();
    }

    /// <summary>
    /// Decodes <paramref name="text"/> into exactly <paramref name="expectedBytes"/> bytes,
    /// ignoring separators and case. Trailing padding bits must be zero, so a mistyped last
    /// character is rejected instead of silently decoding to a different secret.
    /// </summary>
    public static bool TryDecode(string? text, int expectedBytes, out byte[] data)
    {
        data = [];
        if (text is null)
        {
            return false;
        }

        var bytes = new byte[expectedBytes];
        var written = 0;
        var buffer = 0;
        var bits = 0;

        foreach (var raw in text)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            var index = Alphabet.IndexOf(Normalize(raw));
            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits < 8)
            {
                continue;
            }

            bits -= 8;
            if (written == expectedBytes)
            {
                return false;
            }

            bytes[written++] = (byte)((buffer >> bits) & 0xFF);
        }

        // Exactly the right number of bytes, no spare group of characters, and the padding
        // bits zero: a mistyped or truncated code must fail, never decode to another secret.
        if (written != expectedBytes || bits >= 5 || (buffer & ((1 << bits) - 1)) != 0)
        {
            return false;
        }

        data = bytes;
        return true;
    }

    /// <summary>Groups of five, the last group absorbing a remainder too short to stand alone.</summary>
    public static string Group(string text, int size = 5)
    {
        var groups = new List<string>();
        for (var i = 0; i < text.Length; i += size)
        {
            groups.Add(text.Substring(i, Math.Min(size, text.Length - i)));
        }

        if (groups.Count > 1 && groups[^1].Length < 3)
        {
            groups[^2] += groups[^1];
            groups.RemoveAt(groups.Count - 1);
        }

        return string.Join('-', groups);
    }

    private static char Normalize(char c) => char.ToUpperInvariant(c) switch
    {
        'I' or 'L' => '1',
        'O' => '0',
        var other => other,
    };
}

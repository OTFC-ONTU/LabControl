using System.Text;

namespace LabControl.Shared.Packages;

/// <summary>
/// Reads the <c>Property</c> table of a Windows Installer database without Windows: the
/// string pool (<c>_StringPool</c> + <c>_StringData</c>) and the two string columns of
/// <c>Property</c>, stored column after column in the compound file (D-88). Enough for
/// the wizard's proposals — <c>ProductName</c>, <c>ProductVersion</c>, <c>Manufacturer</c>,
/// <c>ProductCode</c> — and nothing else of the database is touched.
/// </summary>
public static class MsiProperties
{
    private const string Mime = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";

    static MsiProperties() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// The compound-file name Windows Installer gives a table's stream: a 0x4840 marker,
    /// then pairs of characters from <see cref="Mime"/> folded into one character each.
    /// </summary>
    public static string EncodeTableName(string name)
    {
        var result = new StringBuilder();
        result.Append('䡀');
        for (var i = 0; i < name.Length; i++)
        {
            var first = Mime.IndexOf(name[i], StringComparison.Ordinal);
            if (first < 0)
            {
                result.Append(name[i]);
                continue;
            }

            var second = i + 1 < name.Length ? Mime.IndexOf(name[i + 1], StringComparison.Ordinal) : -1;
            if (second >= 0)
            {
                result.Append((char)(0x3800 + first + (second << 6)));
                i++;
            }
            else
            {
                result.Append((char)(0x4800 + first));
            }
        }

        return result.ToString();
    }

    /// <summary>Every row of <c>Property</c>; throws <see cref="InvalidDataException"/> for a file that is not a readable database.</summary>
    public static IReadOnlyDictionary<string, string> Read(Stream file)
    {
        var cf = CompoundFile.Open(file);
        var pool = cf.ReadRootStream(EncodeTableName("_StringPool")) ?? throw new InvalidDataException("no string pool");
        var data = cf.ReadRootStream(EncodeTableName("_StringData")) ?? throw new InvalidDataException("no string data");
        var property = cf.ReadRootStream(EncodeTableName("Property")) ?? throw new InvalidDataException("no Property table");
        var strings = ReadStrings(pool, data, out var refSize);

        var rows = property.Length / (2 * refSize);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var row = 0; row < rows; row++)
        {
            var key = Lookup(strings, ReadRef(property, row * refSize, refSize));
            var value = Lookup(strings, ReadRef(property, (rows + row) * refSize, refSize));
            if (!string.IsNullOrEmpty(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// The string pool: a codepage word pair, then a (length, refcount) pair per string id
    /// from 1. A string of 64 KiB or more takes two entries, the first one zero-length with
    /// the high word of its length in the second. Bit 15 of the second codepage word means
    /// the tables refer to strings with three bytes instead of two.
    /// </summary>
    private static List<string> ReadStrings(byte[] pool, byte[] data, out int refSize)
    {
        if (pool.Length < 4)
        {
            throw new InvalidDataException("string pool too short");
        }

        ushort Word(int index) => (ushort)(pool[index * 2] | (pool[index * 2 + 1] << 8));
        var codepage = Word(0) | ((Word(1) & 0x7FFF) << 16);
        refSize = (Word(1) & 0x8000) != 0 ? 3 : 2;
        var encoding = EncodingFor(codepage);

        var strings = new List<string> { string.Empty };
        var count = pool.Length / 4;
        var offset = 0;
        for (var i = 1; i < count;)
        {
            int length = Word(i * 2);
            var refs = Word(i * 2 + 1);
            if (length == 0 && refs == 0)
            {
                strings.Add(string.Empty);
                i++;
                continue;
            }

            if (length == 0)
            {
                if (i + 1 >= count)
                {
                    throw new InvalidDataException("truncated long string entry");
                }

                length = Word(i * 2 + 2) | (Word(i * 2 + 3) << 16);
                i += 2;
            }
            else
            {
                i++;
            }

            if (length < 0 || offset + length > data.Length)
            {
                throw new InvalidDataException("string beyond the string data");
            }

            strings.Add(encoding.GetString(data, offset, length));
            offset += length;
        }

        return strings;
    }

    private static Encoding EncodingFor(int codepage)
    {
        try
        {
            return codepage switch
            {
                0 => Encoding.GetEncoding(1252),
                65001 => Encoding.UTF8,
                _ => Encoding.GetEncoding(codepage),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(1252);
        }
    }

    private static int ReadRef(byte[] table, int offset, int size) =>
        size == 3 ? table[offset] | (table[offset + 1] << 8) | (table[offset + 2] << 16) : table[offset] | (table[offset + 1] << 8);

    private static string Lookup(List<string> strings, int id) => id > 0 && id < strings.Count ? strings[id] : string.Empty;
}

using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace LabControl.Shared.Packages;

/// <summary>
/// The version resource of a Windows executable, read on any OS (D-88): the strings of its
/// first string table (<c>ProductName</c>, <c>ProductVersion</c>, <c>CompanyName</c>,
/// <c>Comments</c>…), the fixed product version, and the PE section names — a WiX bundle is
/// recognised by its <c>.wixburn</c> section.
/// </summary>
public sealed record PeVersionInfo(IReadOnlyDictionary<string, string> Strings, string? FixedProductVersion, IReadOnlyList<string> Sections)
{
    private const int RtVersion = 16;

    public string? Get(string key) => Strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>Reads <paramref name="file"/> (seekable, left open); <c>null</c> when it is not a PE image at all.</summary>
    public static PeVersionInfo? Read(Stream file)
    {
        try
        {
            file.Position = 0;
            using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
            var headers = pe.PEHeaders;
            var sections = headers.SectionHeaders.Select(s => s.Name).ToArray();
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? fixedVersion = null;
            var resources = headers.PEHeader?.ResourceTableDirectory;
            if (resources is { RelativeVirtualAddress: > 0, Size: > 0 } directory
                && FindVersionBlock(pe, directory.RelativeVirtualAddress) is { } block)
            {
                ParseVersionInfo(block, strings, out fixedVersion);
            }

            return new PeVersionInfo(strings, fixedVersion, sections);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException)
        {
            return null;
        }
    }

    /// <summary>Resource tree: type 16 → the first name → the first language → its data entry.</summary>
    private static byte[]? FindVersionBlock(PEReader pe, int resourceRva)
    {
        var root = pe.GetSectionData(resourceRva).GetContent().ToArray();
        (int Offset, bool IsDirectory)? Child(int directoryOffset, int? wantedId)
        {
            if (directoryOffset < 0 || directoryOffset + 16 > root.Length) return null;
            var named = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(directoryOffset + 12));
            var ids = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(directoryOffset + 14));
            for (var i = 0; i < named + ids; i++)
            {
                var entry = directoryOffset + 16 + i * 8;
                if (entry + 8 > root.Length) return null;
                var name = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(entry));
                var target = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(entry + 4));
                if (wantedId is null || ((name & 0x80000000) == 0 && name == wantedId))
                {
                    return ((int)(target & 0x7FFFFFFF), (target & 0x80000000) != 0);
                }
            }

            return null;
        }

        if (Child(0, RtVersion) is not { IsDirectory: true } type) return null;
        if (Child(type.Offset, null) is not { IsDirectory: true } name) return null;
        if (Child(name.Offset, null) is not { IsDirectory: false } language || language.Offset + 16 > root.Length) return null;
        var l = language.Offset;
        var dataRva = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(l));
        var size = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(l + 4));
        if (size <= 0 || size > 1024 * 1024) return null;
        var data = pe.GetSectionData(dataRva).GetContent().ToArray();
        return data.Length >= size ? data.AsSpan(0, size).ToArray() : null;
    }

    /// <summary>
    /// <c>VS_VERSIONINFO</c>: nested blocks of (length, value length, type, key, value,
    /// children), each 32-bit aligned. Strings are taken from the first <c>StringTable</c>.
    /// </summary>
    private static void ParseVersionInfo(byte[] block, Dictionary<string, string> strings, out string? fixedVersion)
    {
        fixedVersion = null;
        var root = ReadBlock(block, 0, block.Length);
        if (root is null) return;
        if (root.Value.ValueLength >= 52 && root.Value.ValueOffset + 52 <= block.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(root.Value.ValueOffset)) == 0xFEEF04BD)
        {
            var ms = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(root.Value.ValueOffset + 16));
            var ls = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(root.Value.ValueOffset + 20));
            fixedVersion = $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
        }

        foreach (var info in Children(block, root.Value))
        {
            if (!string.Equals(info.Key, "StringFileInfo", StringComparison.Ordinal)) continue;
            foreach (var table in Children(block, info))
            {
                foreach (var entry in Children(block, table))
                {
                    var length = Math.Max(0, Math.Min(entry.ValueLength * 2, entry.End - entry.ValueOffset));
                    var text = Encoding.Unicode.GetString(block, entry.ValueOffset, length);
                    var nul = text.IndexOf('\0');
                    strings.TryAdd(entry.Key, nul >= 0 ? text[..nul] : text);
                }

                return;
            }
        }
    }

    private readonly record struct Block(string Key, int ValueOffset, int ValueLength, int ChildrenOffset, int End);

    private static IEnumerable<Block> Children(byte[] data, Block parent)
    {
        var offset = parent.ChildrenOffset;
        while (offset + 6 <= parent.End)
        {
            var child = ReadBlock(data, offset, parent.End);
            if (child is null) yield break;
            yield return child.Value;
            offset = Align(child.Value.End);
        }
    }

    private static Block? ReadBlock(byte[] data, int offset, int limit)
    {
        if (offset + 6 > limit) return null;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
        var end = offset + length;
        if (length < 6 || end > limit) return null;
        var keyStart = offset + 6;
        var keyEnd = keyStart;
        while (keyEnd + 1 < end && (data[keyEnd] != 0 || data[keyEnd + 1] != 0)) keyEnd += 2;
        var key = Encoding.Unicode.GetString(data, keyStart, keyEnd - keyStart);
        var valueOffset = Align(keyEnd + 2);
        var valueBytes = type == 1 ? valueLength * 2 : valueLength;
        var childrenOffset = Align(Math.Min(end, valueOffset + valueBytes));
        return new Block(key, valueOffset, valueLength, childrenOffset, end);
    }

    private static int Align(int offset) => (offset + 3) & ~3;
}

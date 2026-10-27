using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using LabControl.Shared.Packages;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// The <i>Add package</i> wizard's inspection (M7 portion 3, D-88): an MSI's Property table
/// read through a compound file written here byte by byte, an executable's version resource
/// read from a real PE, the family markers, and the catalog entry's own rules.
/// </summary>
public sealed class PackageInspectionTests
{
    private static string Temp(string name, byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "labcontrol-pkg-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static IReadOnlyDictionary<string, string> MsiProperties_ =>
        new Dictionary<string, string>
        {
            ["ProductName"] = "Eclipse Temurin JDK with Hotspot 21.0.5+11 (x64)",
            ["ProductVersion"] = "21.0.5.11",
            ["Manufacturer"] = "Eclipse Adoptium",
            ["ProductCode"] = "{9A8B7C6D-0000-4000-8000-123456789ABC}",
            ["ALLUSERS"] = "1",
        };

    [Fact]
    public async Task An_msi_gives_its_product_name_version_maker_and_code()
    {
        var path = Temp("OpenJDK21.msi", MsiBuilder.Build(MsiProperties_));
        var info = await InstallerInspector.InspectAsync(path, null, TestContext.Current.CancellationToken);

        Assert.Equal(InstallerFamily.Msi, info.Family);
        Assert.Equal("Eclipse Temurin JDK with Hotspot 21.0.5+11 (x64)", info.ProductName);
        Assert.Equal("21.0.5.11", info.ProductVersion);
        Assert.Equal("Eclipse Adoptium", info.Manufacturer);
        Assert.Equal("{9A8B7C6D-0000-4000-8000-123456789ABC}", info.ProductCode);
        Assert.Equal(new FileInfo(path).Length, info.Size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), info.Sha256);
        Assert.Equal("/qn /norestart", InstallerInspector.SilentArguments(info.Family));
    }

    [Fact]
    public void Three_byte_string_references_long_strings_and_streams_outside_the_mini_stream_are_read()
    {
        var properties = new Dictionary<string, string>(MsiProperties_)
        {
            ["Filler"] = new string('x', 70_000),
            ["ProductName"] = "Програма",
        };
        var bytes = MsiBuilder.Build(properties, longRefs: true, codepage: 65001);
        var read = MsiProperties.Read(new MemoryStream(bytes));

        Assert.Equal("Програма", read["ProductName"]);
        Assert.Equal(70_000, read["Filler"].Length);
        Assert.Equal("Eclipse Adoptium", read["Manufacturer"]);
    }

    [Fact]
    public async Task A_damaged_compound_file_is_an_unknown_installer_and_never_a_hang()
    {
        var bytes = MsiBuilder.Build(MsiProperties_);
        // Every FAT entry points at sector 0: each chain becomes a loop.
        for (var i = 512; i < 1024; i += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i), 0);
        }

        var info = await InstallerInspector.InspectAsync(Temp("broken.msi", bytes), null, TestContext.Current.CancellationToken);
        Assert.Equal(InstallerFamily.Unknown, info.Family);
        Assert.Null(info.ProductName);

        var truncated = MsiBuilder.Build(MsiProperties_)[..1500];
        Assert.Equal(InstallerFamily.Unknown, (await InstallerInspector.InspectAsync(Temp("short.msi", truncated), null, TestContext.Current.CancellationToken)).Family);
    }

    [Fact]
    public void The_table_stream_names_are_encoded_as_windows_installer_does()
    {
        // Two characters of the 64-symbol alphabet fold into one, a lone last one gets 0x4800.
        var encoded = MsiProperties.EncodeTableName("Property");
        Assert.Equal('\u4840', encoded[0]);
        Assert.Equal(5, encoded.Length);
        Assert.Equal((char)(0x3800 + 25 + (53 << 6)), encoded[1]); // 'P' = 25, 'r' = 53
        Assert.Equal((char)(0x4800 + 63), MsiProperties.EncodeTableName("_")[1]);
    }

    /// <summary>A real managed DLL is a PE with a version resource; .NET's own reader is the oracle.</summary>
    private static string RealPe() => typeof(Google.Protobuf.MessageParser).Assembly.Location;

    [Fact]
    public async Task An_executable_gives_its_version_resource()
    {
        var path = RealPe();
        var expected = FileVersionInfo.GetVersionInfo(path);
        var info = await InstallerInspector.InspectAsync(path, null, TestContext.Current.CancellationToken);

        Assert.Equal(InstallerFamily.Unknown, info.Family);
        Assert.Equal(expected.ProductName, info.ProductName);
        Assert.Equal(expected.CompanyName, info.Manufacturer);
        Assert.False(string.IsNullOrEmpty(info.ProductVersion));
        Assert.StartsWith(info.ProductVersion!, expected.ProductVersion!, StringComparison.Ordinal);
        Assert.Equal(string.Empty, InstallerInspector.SilentArguments(info.Family));
    }

    [Theory]
    [InlineData("nsis", InstallerFamily.Nsis, "/S")]
    [InlineData("inno", InstallerFamily.Inno, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")]
    [InlineData("installshield", InstallerFamily.InstallShield, "/s /v\"/qn /norestart\"")]
    [InlineData("burn", InstallerFamily.WixBurn, "/quiet /norestart")]
    public async Task Each_family_is_recognised_by_its_marker(string kind, InstallerFamily family, string silent)
    {
        var bytes = File.ReadAllBytes(RealPe());
        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
        bytes = kind switch
        {
            // NSIS's first header follows the stub: flags, 0xDEADBEEF, "NullsoftInst".
            "nsis" => [.. bytes, 0, 0, 0, 0, 0xEF, 0xBE, 0xAD, 0xDE, .. Ascii("NullsoftInst")],
            "inno" => [.. bytes, .. Ascii("Inno Setup Setup Data (6.4.0) (u)")],
            "installshield" => [.. bytes, .. Ascii("InstallShield Self-extracting")],
            _ => RenameSection(bytes, ".wixburn"),
        };
        var info = await InstallerInspector.InspectAsync(Temp("setup.exe", bytes), null, TestContext.Current.CancellationToken);
        Assert.Equal(family, info.Family);
        Assert.Equal(silent, InstallerInspector.SilentArguments(family));
    }

    private static byte[] RenameSection(byte[] pe, string name)
    {
        var header = pe.AsSpan(0, 2048);
        var at = header.IndexOf(".reloc\0\0"u8);
        if (at < 0) at = header.IndexOf(".text\0\0\0"u8);
        Assert.True(at >= 0);
        var copy = pe.ToArray();
        Encoding.ASCII.GetBytes(name.PadRight(8, '\0')).CopyTo(copy, at);
        return copy;
    }

    [Fact]
    public async Task A_file_that_is_neither_gives_only_its_hash()
    {
        var info = await InstallerInspector.InspectAsync(Temp("notes.exe", "hello"u8.ToArray()), null, TestContext.Current.CancellationToken);
        Assert.Equal(InstallerFamily.Unknown, info.Family);
        Assert.Null(info.ProductName);
        Assert.Equal(5, info.Size);
        Assert.Equal(64, info.Sha256.Length);
    }

    [Theory]
    [InlineData("IntelliJ IDEA Community Edition", "intellij-idea-community-edition")]
    [InlineData("  7-Zip 24.08 (x64)  ", "7-zip-24-08-x64")]
    [InlineData("Програма", "package")]
    [InlineData("Café", "cafe")]
    public void Ids_are_lower_case_ascii_words(string name, string id)
    {
        Assert.Equal(id, PackageEntry.IdFor(name));
        Assert.True(PackageEntry.IsValidId(id));
    }

    [Theory]
    [InlineData("C:\\Program Files\\App\\app.exe", true)]
    [InlineData("%ProgramFiles%\\App\\app.exe", true)]
    [InlineData("%ProgramFiles(x86)%\\App\\app.exe", true)]
    [InlineData("C:/Program Files/App/app.exe", false)]
    [InlineData("C:\\Program Files\\..\\Windows\\x.exe", false)]
    [InlineData("app.exe", false)]
    [InlineData("\\\\server\\share\\app.exe", false)]
    [InlineData("%%\\app.exe", false)]
    public void Detect_paths_are_absolute_windows_paths(string path, bool valid) => Assert.Equal(valid, PackageEntry.IsWindowsPath(path));

    [Fact]
    public void An_entry_is_checked_before_it_is_saved()
    {
        var entry = new PackageEntry
        {
            Id = "jdk", Name = "JDK", Version = "21", Family = InstallerFamily.Msi, File = "jdk.msi", Size = 1,
            Sha256 = new string('a', 64), Silent = "/qn", Detect = new DetectRule(DetectKind.UninstallName, "Eclipse Temurin"),
        };
        Assert.Equal(PackageProblem.None, entry.Validate());
        Assert.Equal(PackageProblem.NameMissing, (entry with { Name = " " }).Validate());
        Assert.Equal(PackageProblem.MsiFileExpected, (entry with { File = "jdk.exe" }).Validate());
        Assert.Equal(PackageProblem.FileNameInvalid, (entry with { File = "../jdk.msi" }).Validate());
        Assert.Equal(PackageProblem.HashInvalid, (entry with { Sha256 = "abc" }).Validate());
        Assert.Equal(PackageProblem.SilentInvalid, (entry with { Silent = "/qn\n/x" }).Validate());
        Assert.Equal(PackageProblem.DetectMissing, (entry with { Detect = new DetectRule(DetectKind.UninstallName, "") }).Validate());
        Assert.Equal(PackageProblem.DetectPathInvalid, (entry with { Detect = new DetectRule(DetectKind.Path, "jdk/bin") }).Validate());
    }
}

/// <summary>
/// Writes a minimal Windows Installer database: a version-3 compound file whose root holds
/// <c>_StringPool</c>, <c>_StringData</c> and <c>Property</c>, small streams in the mini stream
/// and large ones in sectors of their own, as MS-CFB requires.
/// </summary>
internal static class MsiBuilder
{
    private const uint End = 0xFFFFFFFE;
    private const uint Free = 0xFFFFFFFF;
    private const uint FatSect = 0xFFFFFFFD;

    public static byte[] Build(IReadOnlyDictionary<string, string> properties, bool longRefs = false, int codepage = 1252)
    {
        var encoding = codepage == 65001 ? Encoding.UTF8 : Encoding.Latin1;
        var strings = new List<string>();
        int Id(string s)
        {
            var i = strings.IndexOf(s);
            if (i < 0) { strings.Add(s); i = strings.Count - 1; }
            return i + 1;
        }

        var rows = properties.Select(p => (Key: Id(p.Key), Value: Id(p.Value))).ToArray();
        var pool = new MemoryStream();
        var data = new MemoryStream();
        void Word(int w) => pool.Write([(byte)w, (byte)(w >> 8)]);
        Word(codepage & 0xFFFF);
        Word(((codepage >> 16) & 0x7FFF) | (longRefs ? 0x8000 : 0));
        foreach (var s in strings)
        {
            var bytes = encoding.GetBytes(s);
            if (bytes.Length > 0xFFFF)
            {
                Word(0); Word(1); Word(bytes.Length & 0xFFFF); Word(bytes.Length >> 16);
            }
            else
            {
                Word(bytes.Length); Word(1);
            }

            data.Write(bytes);
        }

        var refSize = longRefs ? 3 : 2;
        var table = new byte[rows.Length * 2 * refSize];
        for (var r = 0; r < rows.Length; r++)
        {
            Ref(table, r * refSize, rows[r].Key, refSize);
            Ref(table, (rows.Length + r) * refSize, rows[r].Value, refSize);
        }

        return Compound(
        [
            (MsiProperties.EncodeTableName("_StringPool"), pool.ToArray()),
            (MsiProperties.EncodeTableName("_StringData"), data.ToArray()),
            (MsiProperties.EncodeTableName("Property"), table),
            ("\u0005SummaryInformation", new byte[48]),
        ]);
    }

    private static void Ref(byte[] table, int offset, int id, int size)
    {
        table[offset] = (byte)id;
        table[offset + 1] = (byte)(id >> 8);
        if (size == 3) table[offset + 2] = (byte)(id >> 16);
    }

    private static byte[] Compound(IReadOnlyList<(string Name, byte[] Data)> streams)
    {
        const int sector = 512;
        const int mini = 64;
        var miniStream = new MemoryStream();
        var miniFat = new List<uint>();
        var starts = new uint[streams.Count];
        var big = new List<int>();
        for (var i = 0; i < streams.Count; i++)
        {
            var bytes = streams[i].Data;
            if (bytes.Length >= 4096) { big.Add(i); continue; }
            var count = Math.Max(1, (bytes.Length + mini - 1) / mini);
            starts[i] = (uint)miniFat.Count;
            for (var k = 0; k < count; k++) miniFat.Add(k == count - 1 ? End : (uint)(miniFat.Count + 1));
            miniStream.Write(bytes);
            miniStream.Write(new byte[count * mini - bytes.Length]);
        }

        byte[] Pad(byte[] b) => [.. b, .. new byte[(sector - b.Length % sector) % sector]];
        var directory = new byte[((streams.Count + 1 + 3) / 4) * 4 * 128];
        var miniFatBytes = Pad(miniFat.SelectMany(BitConverter.GetBytes).ToArray());
        var miniBytes = Pad(miniStream.ToArray());
        var blobs = new List<byte[]> { directory, miniFatBytes, miniBytes };
        blobs.AddRange(big.Select(i => Pad(streams[i].Data)));

        var others = blobs.Sum(b => b.Length / sector);
        var fatSectors = 1;
        while (fatSectors * 128 < fatSectors + others) fatSectors++;
        var fat = Enumerable.Repeat(Free, fatSectors * 128).ToArray();
        for (var f = 0; f < fatSectors; f++) fat[f] = FatSect;
        var next = (uint)fatSectors;
        var blobStarts = new List<uint>();
        foreach (var blob in blobs)
        {
            var count = blob.Length / sector;
            blobStarts.Add(count == 0 ? End : next);
            for (var k = 0; k < count; k++) fat[next + k] = k == count - 1 ? End : next + (uint)k + 1;
            next += (uint)count;
        }

        for (var j = 0; j < big.Count; j++) starts[big[j]] = blobStarts[3 + j];

        void Entry(int index, string name, byte type, uint right, uint child, uint start, long size)
        {
            var e = directory.AsSpan(index * 128, 128);
            var nameBytes = Encoding.Unicode.GetBytes(name + "\0");
            nameBytes.CopyTo(e);
            BinaryPrimitives.WriteUInt16LittleEndian(e[64..], (ushort)nameBytes.Length);
            e[66] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(e[68..], Free);
            BinaryPrimitives.WriteUInt32LittleEndian(e[72..], right);
            BinaryPrimitives.WriteUInt32LittleEndian(e[76..], child);
            BinaryPrimitives.WriteUInt32LittleEndian(e[116..], start);
            BinaryPrimitives.WriteUInt32LittleEndian(e[120..], (uint)size);
        }

        for (var i = 0; i < directory.Length / 128; i++) Entry(i, string.Empty, 0, Free, Free, 0, 0);
        Entry(0, "Root Entry", 5, Free, streams.Count > 0 ? 1u : Free, miniStream.Length > 0 ? blobStarts[2] : End, miniStream.Length);
        for (var i = 0; i < streams.Count; i++)
        {
            Entry(i + 1, streams[i].Name, 2, i + 1 < streams.Count ? (uint)(i + 2) : Free, Free, starts[i], streams[i].Data.Length);
        }

        var header = new byte[sector];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30), blobStarts[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), miniFat.Count > 0 ? blobStarts[1] : End);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), (uint)(miniFatBytes.Length / sector));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44), End);
        for (var i = 0; i < 109; i++) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + i * 4), i < fatSectors ? (uint)i : Free);

        var file = new MemoryStream();
        file.Write(header);
        file.Write(fat.SelectMany(BitConverter.GetBytes).ToArray());
        foreach (var blob in blobs) file.Write(blob);
        return file.ToArray();
    }
}

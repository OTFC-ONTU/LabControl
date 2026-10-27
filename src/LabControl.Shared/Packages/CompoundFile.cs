using System.Buffers.Binary;
using System.Text;

namespace LabControl.Shared.Packages;

/// <summary>
/// A read-only view of the streams at the root of an OLE compound file — the container an
/// <c>.msi</c> is (MS-CFB). Only what the <i>Add package</i> wizard needs: the root storage's
/// streams by name, read whole. Every chain is bounded by the file's own sector count, so a
/// damaged or hostile file ends in <see cref="InvalidDataException"/>, never in a loop (D-88).
/// </summary>
public sealed class CompoundFile
{
    public static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint MaxRegularSector = 0xFFFFFFFA;
    private const int HeaderSize = 512;
    private const int DirectoryEntrySize = 128;
    private const uint NoStream = 0xFFFFFFFF;

    /// <summary>The largest stream read whole; the MSI tables the wizard reads are far smaller.</summary>
    public const int MaxStreamLength = 64 * 1024 * 1024;

    private readonly Stream _file;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<Entry> _entries;
    private readonly long _sectorCount;
    private byte[]? _miniStream;

    private sealed record Entry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, long Size);

    private CompoundFile(Stream file)
    {
        _file = file;
        var header = ReadAt(0, HeaderSize);
        if (!header.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a compound file");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1A));
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E));
        var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x20));
        if (!((major == 3 && sectorShift == 9) || (major == 4 && sectorShift == 12)) || miniShift != 6)
        {
            throw new InvalidDataException("unsupported compound file version");
        }

        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniShift;
        _sectorCount = Math.Max(0, (file.Length - _sectorSize) / _sectorSize + 1);
        var fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x2C));
        var firstDirectory = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30));
        _miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38));
        var firstMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x3C));
        var firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x44));
        if (fatSectors > _sectorCount)
        {
            throw new InvalidDataException("FAT larger than the file");
        }

        // The DIFAT: 109 FAT sector numbers in the header, the rest in a chain of DIFAT sectors.
        var fatSectorList = new List<uint>();
        for (var i = 0; i < 109 && fatSectorList.Count < fatSectors; i++)
        {
            fatSectorList.Add(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x4C + i * 4)));
        }

        var difat = firstDifat;
        for (long guard = 0; fatSectorList.Count < fatSectors; guard++)
        {
            if (difat > MaxRegularSector || guard > _sectorCount)
            {
                throw new InvalidDataException("broken DIFAT chain");
            }

            var sector = ReadSector(difat);
            var perSector = _sectorSize / 4 - 1;
            for (var i = 0; i < perSector && fatSectorList.Count < fatSectors; i++)
            {
                fatSectorList.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i * 4)));
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(perSector * 4));
        }

        _fat = new uint[fatSectorList.Count * (_sectorSize / 4)];
        for (var s = 0; s < fatSectorList.Count; s++)
        {
            var sector = ReadSector(fatSectorList[s]);
            for (var i = 0; i < _sectorSize / 4; i++)
            {
                _fat[s * (_sectorSize / 4) + i] = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i * 4));
            }
        }

        var directory = ReadChain(firstDirectory, long.MaxValue);
        _entries = [];
        for (var offset = 0; offset + DirectoryEntrySize <= directory.Length; offset += DirectoryEntrySize)
        {
            var e = directory.AsSpan(offset, DirectoryEntrySize);
            var nameBytes = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(e[64..]), 0, 64);
            var name = Encoding.Unicode.GetString(e[..Math.Max(0, nameBytes - 2)]);
            var size = major == 3
                ? BinaryPrimitives.ReadUInt32LittleEndian(e[120..])
                : (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(e[120..]), long.MaxValue);
            _entries.Add(new Entry(name, e[66],
                BinaryPrimitives.ReadUInt32LittleEndian(e[68..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[72..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[76..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[116..]),
                size));
        }

        if (_entries.Count == 0 || _entries[0].Type != 5)
        {
            throw new InvalidDataException("no root entry");
        }

        var miniFatSectors = firstMiniFat > MaxRegularSector ? [] : ReadChain(firstMiniFat, long.MaxValue);
        _miniFat = new uint[miniFatSectors.Length / 4];
        for (var i = 0; i < _miniFat.Length; i++)
        {
            _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatSectors.AsSpan(i * 4));
        }
    }

    /// <summary>Opens <paramref name="file"/> (seekable, left open); throws <see cref="InvalidDataException"/> for anything not a readable compound file.</summary>
    public static CompoundFile Open(Stream file)
    {
        try
        {
            return new CompoundFile(file);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException)
        {
            throw new InvalidDataException("damaged compound file", ex);
        }
    }

    /// <summary>The names of the streams directly in the root storage.</summary>
    public IReadOnlyList<string> RootStreams() => RootChildren().Where(e => e.Type == 2).Select(e => e.Name).ToArray();

    /// <summary>The whole stream <paramref name="name"/> of the root storage, or <c>null</c> when there is none.</summary>
    public byte[]? ReadRootStream(string name)
    {
        var entry = RootChildren().FirstOrDefault(e => e.Type == 2 && string.Equals(e.Name, name, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        if (entry.Size > MaxStreamLength)
        {
            throw new InvalidDataException("stream too large");
        }

        try
        {
            if (entry.Size < _miniStreamCutoff)
            {
                _miniStream ??= ReadChain(_entries[0].Start, _entries[0].Size);
                return ReadMiniChain(entry.Start, entry.Size);
            }

            return ReadChain(entry.Start, entry.Size);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException)
        {
            throw new InvalidDataException("damaged stream", ex);
        }
    }

    /// <summary>The root's children: an in-order walk of its red-black sibling tree, each entry at most once.</summary>
    private List<Entry> RootChildren()
    {
        var result = new List<Entry>();
        var seen = new HashSet<uint>();
        var pending = new Stack<uint>();
        pending.Push(_entries[0].Child);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id == NoStream || id >= _entries.Count || !seen.Add(id))
            {
                continue;
            }

            var entry = _entries[(int)id];
            result.Add(entry);
            pending.Push(entry.Left);
            pending.Push(entry.Right);
        }

        return result;
    }

    private byte[] ReadChain(uint start, long size)
    {
        using var buffer = new MemoryStream();
        var sector = start;
        for (long guard = 0; sector != EndOfChain; guard++)
        {
            if (sector > MaxRegularSector || sector >= _fat.Length || guard > _sectorCount)
            {
                throw new InvalidDataException("broken sector chain");
            }

            buffer.Write(ReadSector(sector));
            if (buffer.Length >= size)
            {
                break;
            }

            if (buffer.Length > MaxStreamLength + (long)_sectorSize)
            {
                throw new InvalidDataException("stream too large");
            }

            sector = _fat[sector];
        }

        var bytes = buffer.ToArray();
        if (size != long.MaxValue && bytes.Length < size)
        {
            throw new InvalidDataException("stream shorter than its size");
        }

        return size == long.MaxValue ? bytes : bytes[..(int)size];
    }

    private byte[] ReadMiniChain(uint start, long size)
    {
        var result = new byte[size];
        var written = 0;
        var sector = start;
        for (long guard = 0; written < size; guard++)
        {
            if (sector > MaxRegularSector || sector >= _miniFat.Length || guard > _miniFat.Length)
            {
                throw new InvalidDataException("broken mini sector chain");
            }

            var offset = (long)sector * _miniSectorSize;
            var count = (int)Math.Min(_miniSectorSize, size - written);
            if (offset + count > _miniStream!.Length)
            {
                throw new InvalidDataException("mini sector outside the mini stream");
            }

            Array.Copy(_miniStream, offset, result, written, count);
            written += count;
            sector = _miniFat[sector];
        }

        return result;
    }

    private byte[] ReadSector(uint sector) => ReadAt(((long)sector + 1) * _sectorSize, _sectorSize);

    private byte[] ReadAt(long offset, int count)
    {
        if (offset < 0 || offset + count > _file.Length)
        {
            throw new InvalidDataException("read beyond the end of the file");
        }

        var bytes = new byte[count];
        _file.Position = offset;
        _file.ReadExactly(bytes);
        return bytes;
    }
}

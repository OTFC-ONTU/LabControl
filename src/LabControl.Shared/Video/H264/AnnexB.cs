namespace LabControl.Shared.Video.H264;

/// <summary>
/// The H.264 byte stream as it travels in a <c>VideoFrame</c> (D-91 item 2): NAL units behind
/// <c>00 00 01</c> or <c>00 00 00 01</c> start codes. VideoToolbox speaks length-prefixed
/// units instead, so this converts both ways and finds the parameter sets and the IDR.
/// </summary>
public static class AnnexB
{
    public const int NalSlice = 1;
    public const int NalIdr = 5;
    public const int NalSps = 7;
    public const int NalPps = 8;

    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    /// <summary>The NAL units of an Annex B access unit, without their start codes, as (offset, length).</summary>
    public static List<(int Offset, int Length)> Units(ReadOnlySpan<byte> stream)
    {
        var units = new List<(int, int)>();
        var start = -1;
        var i = 0;
        while (i + 2 < stream.Length)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                if (start >= 0)
                {
                    // A four-byte start code leaves its leading zero on the unit before it.
                    var end = i;
                    while (end > start && stream[end - 1] == 0)
                    {
                        end--;
                    }

                    if (end > start)
                    {
                        units.Add((start, end - start));
                    }
                }

                i += 3;
                start = i;
                continue;
            }

            i++;
        }

        if (start >= 0 && start < stream.Length)
        {
            units.Add((start, stream.Length - start));
        }

        return units;
    }

    public static int TypeOf(byte header) => header & 0x1F;

    /// <summary>The access unit holds an IDR slice — a picture that needs nothing before it.</summary>
    public static bool IsIdr(ReadOnlySpan<byte> stream) => Contains(stream, NalIdr);

    public static bool Contains(ReadOnlySpan<byte> stream, int type)
    {
        foreach (var (offset, length) in Units(stream))
        {
            if (length > 0 && TypeOf(stream[offset]) == type)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first SPS and PPS of the access unit, without start codes; <c>null</c> when either is missing.</summary>
    public static (byte[] Sps, byte[] Pps)? ParameterSets(ReadOnlySpan<byte> stream)
    {
        byte[]? sps = null;
        byte[]? pps = null;
        foreach (var (offset, length) in Units(stream))
        {
            if (length == 0)
            {
                continue;
            }

            var type = TypeOf(stream[offset]);
            if (type == NalSps && sps is null)
            {
                sps = stream.Slice(offset, length).ToArray();
            }
            else if (type == NalPps && pps is null)
            {
                pps = stream.Slice(offset, length).ToArray();
            }
        }

        return sps is not null && pps is not null ? (sps, pps) : null;
    }

    /// <summary>
    /// The access unit's units as 4-byte big-endian length prefixes (AVCC), leaving out the
    /// parameter sets and access-unit delimiters a decoder takes separately; empty when nothing else is left.
    /// </summary>
    public static byte[] ToLengthPrefixed(ReadOnlySpan<byte> stream)
    {
        var units = Units(stream);
        var size = 0;
        foreach (var (offset, length) in units)
        {
            if (Keeps(stream[offset]))
            {
                size += 4 + length;
            }
        }

        var result = new byte[size];
        var at = 0;
        foreach (var (offset, length) in units)
        {
            if (!Keeps(stream[offset]))
            {
                continue;
            }

            result[at] = (byte)(length >> 24);
            result[at + 1] = (byte)(length >> 16);
            result[at + 2] = (byte)(length >> 8);
            result[at + 3] = (byte)length;
            stream.Slice(offset, length).CopyTo(result.AsSpan(at + 4));
            at += 4 + length;
        }

        return result;

        static bool Keeps(byte header) => TypeOf(header) is not (NalSps or NalPps or 9);
    }

    /// <summary>Appends length-prefixed (AVCC, 4-byte lengths) units to <paramref name="output"/> as Annex B; <c>false</c> on a malformed length.</summary>
    public static bool AppendLengthPrefixed(ReadOnlySpan<byte> units, List<byte> output)
    {
        var at = 0;
        while (at + 4 <= units.Length)
        {
            var length = (units[at] << 24) | (units[at + 1] << 16) | (units[at + 2] << 8) | units[at + 3];
            at += 4;
            if (length <= 0 || length > units.Length - at)
            {
                return false;
            }

            Append(units.Slice(at, length), output);
            at += length;
        }

        return at == units.Length;
    }

    /// <summary>Appends one NAL unit (no start code) to <paramref name="output"/> behind a four-byte start code.</summary>
    public static void Append(ReadOnlySpan<byte> unit, List<byte> output)
    {
        output.AddRange(StartCode);
        output.AddRange(unit);
    }
}

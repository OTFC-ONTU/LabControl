using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace LabControl.Shared.Internet;

/// <summary>The one question of a DNS message: its name as the wire spells it, type and class.</summary>
/// <param name="Name">The name lowercased, labels joined with dots, no trailing dot; a byte that is not a printable ASCII character other than a dot is written <c>\DDD</c>, so such a name never matches a pattern.</param>
/// <param name="Type">The query type (1 A, 28 AAAA, 64 SVCB, 65 HTTPS, …).</param>
/// <param name="Class">The query class (1 IN).</param>
public readonly record struct DnsQuestion(string Name, ushort Type, ushort Class);

/// <summary>A parsed query, enough to forward it or answer it ourselves.</summary>
/// <param name="Id">The client's message id.</param>
/// <param name="Flags">The header's flag word as sent.</param>
/// <param name="Question">The single question.</param>
/// <param name="QuestionEnd">The offset just past the question, so a reply can echo the question's bytes.</param>
/// <param name="UdpPayloadLimit">The largest UDP reply the client accepts: its EDNS size, at least 512.</param>
public sealed record DnsQuery(ushort Id, ushort Flags, DnsQuestion Question, int QuestionEnd, int UdpPayloadLimit);

/// <summary>An address record of an answer section.</summary>
public readonly record struct DnsAddressRecord(IPAddress Address, uint Ttl);

/// <summary>A parsed upstream response: what the resolver needs to judge and relay it.</summary>
/// <param name="Id">The message id.</param>
/// <param name="ResponseCode">The RCODE (0 NOERROR, 2 SERVFAIL, 3 NXDOMAIN, 5 REFUSED, …).</param>
/// <param name="Truncated">The TC bit: the answer did not fit and must be asked again over TCP.</param>
/// <param name="Question">The first question echoed, or <c>null</c> when the response carried none.</param>
/// <param name="Addresses">Every A and AAAA record of the answer section, CNAME chain included, in order.</param>
public sealed record DnsResponse(ushort Id, int ResponseCode, bool Truncated, DnsQuestion? Question, IReadOnlyList<DnsAddressRecord> Addresses);

/// <summary>
/// The little of RFC 1035 the whitelist resolver needs (M6 portion 3, D-74): read a query's
/// question, answer it ourselves with REFUSED, NODATA or SERVFAIL, and read the A/AAAA records
/// out of an upstream answer. Every reader takes untrusted bytes and returns <c>false</c> on
/// anything malformed — truncated sections, a compression pointer that loops or points forward,
/// a name past 255 bytes — and never throws.
/// </summary>
public static class DnsMessage
{
    /// <summary>The fixed header length.</summary>
    public const int HeaderLength = 12;

    /// <summary>The largest UDP reply a client without EDNS accepts.</summary>
    public const int ClassicUdpLimit = 512;

    /// <summary>The largest DNS message there is (the TCP length prefix is 16 bits).</summary>
    public const int MaxMessageLength = 65535;

    /// <summary>Record types the resolver cares about.</summary>
    public static class Types
    {
        public const ushort A = 1;
        public const ushort Cname = 5;
        public const ushort Ptr = 12;
        public const ushort Aaaa = 28;
        public const ushort Opt = 41;
        public const ushort Svcb = 64;
        public const ushort Https = 65;
    }

    /// <summary>Response codes the resolver sends or reads.</summary>
    public static class ResponseCodes
    {
        public const int NoError = 0;
        public const int FormatError = 1;
        public const int ServerFailure = 2;
        public const int NameError = 3;
        public const int NotImplemented = 4;
        public const int Refused = 5;
    }

    private const ushort ClassIn = 1;
    private const ushort FlagResponse = 0x8000;
    private const ushort FlagTruncated = 0x0200;
    private const ushort FlagRecursionDesired = 0x0100;
    private const ushort FlagRecursionAvailable = 0x0080;
    private const int OpcodeMask = 0x7800;
    private const int MaxNameWireLength = 255;
    private const int MaxPointerJumps = 32;

    /// <summary>
    /// Reads a standard query with exactly one question. A response (QR set), another opcode,
    /// zero or several questions, or a message that does not parse is refused.
    /// </summary>
    public static bool TryParseQuery(ReadOnlySpan<byte> message, out DnsQuery query)
    {
        query = null!;
        if (message.Length < HeaderLength || message.Length > MaxMessageLength)
        {
            return false;
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & FlagResponse) != 0 || (flags & OpcodeMask) != 0)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(message[4..]) != 1)
        {
            return false;
        }

        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var authority = BinaryPrimitives.ReadUInt16BigEndian(message[8..]);
        var additional = BinaryPrimitives.ReadUInt16BigEndian(message[10..]);

        var offset = HeaderLength;
        if (!TryReadQuestion(message, ref offset, out var question))
        {
            return false;
        }

        var questionEnd = offset;
        var limit = ClassicUdpLimit;

        // The EDNS OPT record (in the additional section) carries the client's UDP size; a
        // query with records we cannot walk is still a query, it just keeps the classic limit.
        var walk = offset;
        var ok = true;
        for (var i = 0; ok && i < answers + authority; i++)
        {
            ok = TryReadRecord(message, ref walk, out _, out _, out _, out _);
        }

        for (var i = 0; ok && i < additional; i++)
        {
            ok = TryReadRecord(message, ref walk, out var type, out var cls, out _, out _);
            if (ok && type == Types.Opt)
            {
                limit = Math.Max(ClassicUdpLimit, (int)cls);
            }
        }

        query = new DnsQuery(id, flags, question, questionEnd, limit);
        return true;
    }

    /// <summary>A reply with RCODE 5: this name is not on the whitelist.</summary>
    public static byte[] BuildRefused(ReadOnlySpan<byte> message, DnsQuery query) =>
        BuildReply(message, query, ResponseCodes.Refused);

    /// <summary>A reply with RCODE 0 and no answers: the name exists but has no record of this type.</summary>
    public static byte[] BuildNoData(ReadOnlySpan<byte> message, DnsQuery query) =>
        BuildReply(message, query, ResponseCodes.NoError);

    /// <summary>A reply with RCODE 2: no upstream answered, or the firewall could not take the addresses.</summary>
    public static byte[] BuildServerFailure(ReadOnlySpan<byte> message, DnsQuery query) =>
        BuildReply(message, query, ResponseCodes.ServerFailure);

    /// <summary>
    /// A reply of our own to <paramref name="query"/>: the same id, QR and RA set, RD copied,
    /// the question echoed byte for byte and no records.
    /// </summary>
    public static byte[] BuildReply(ReadOnlySpan<byte> message, DnsQuery query, int responseCode)
    {
        ArgumentNullException.ThrowIfNull(query);

        var question = message[HeaderLength..query.QuestionEnd];
        var reply = new byte[HeaderLength + question.Length];
        var flags = (ushort)(FlagResponse | FlagRecursionAvailable | (query.Flags & FlagRecursionDesired) | (responseCode & 0x0F));
        BinaryPrimitives.WriteUInt16BigEndian(reply, query.Id);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), 1);
        question.CopyTo(reply.AsSpan(HeaderLength));
        return reply;
    }

    /// <summary>
    /// A UDP-sized stand-in for an answer that is too big for the client: the answer's header
    /// with TC set and its question, no records, so the client asks again over TCP.
    /// </summary>
    public static byte[] BuildTruncated(ReadOnlySpan<byte> answer, DnsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return BuildReply(answer, query, ResponseCodes.NoError, fromAnswer: true);
    }

    /// <summary>
    /// Reads an upstream response: the header, the first question, and every A/AAAA record of
    /// the answer section (class IN, the right RDATA length). A truncated response may end
    /// mid-section; what was read is returned with <see cref="DnsResponse.Truncated"/> set.
    /// </summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> message, out DnsResponse response)
    {
        response = null!;
        if (message.Length < HeaderLength || message.Length > MaxMessageLength)
        {
            return false;
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & FlagResponse) == 0)
        {
            return false;
        }

        var truncated = (flags & FlagTruncated) != 0;
        var rcode = flags & 0x0F;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

        var offset = HeaderLength;
        DnsQuestion? first = null;
        var addresses = new List<DnsAddressRecord>();
        for (var i = 0; i < questions; i++)
        {
            if (!TryReadQuestion(message, ref offset, out var question))
            {
                if (truncated)
                {
                    response = new DnsResponse(id, rcode, truncated, first, addresses);
                    return true;
                }

                return false;
            }

            first ??= question;
        }

        for (var i = 0; i < answers; i++)
        {
            if (!TryReadRecord(message, ref offset, out var type, out var cls, out var ttl, out var data))
            {
                if (truncated)
                {
                    break;
                }

                return false;
            }

            if (cls != ClassIn)
            {
                continue;
            }

            if (type == Types.A && data.Length == 4)
            {
                addresses.Add(new DnsAddressRecord(new IPAddress(message.Slice(data.Start, 4)), ttl));
            }
            else if (type == Types.Aaaa && data.Length == 16)
            {
                addresses.Add(new DnsAddressRecord(new IPAddress(message.Slice(data.Start, 16)), ttl));
            }
        }

        response = new DnsResponse(id, rcode, truncated, first, addresses);
        return true;
    }

    /// <summary>Overwrites the message id in place (the first two bytes).</summary>
    public static void WriteId(Span<byte> message, ushort id)
    {
        if (message.Length >= 2)
        {
            BinaryPrimitives.WriteUInt16BigEndian(message, id);
        }
    }

    /// <summary>
    /// Reads a possibly compressed name starting at <paramref name="offset"/> and moves the
    /// offset past it (past the first pointer, if any). A pointer must point strictly backwards
    /// from the label that holds it and at most <see cref="MaxPointerJumps"/> are followed, so a
    /// hostile message can neither loop nor read ahead; the expanded name may not pass 255 bytes.
    /// </summary>
    public static bool TryReadName(ReadOnlySpan<byte> message, ref int offset, out string name)
    {
        name = string.Empty;
        var builder = new StringBuilder();
        var position = offset;
        var end = -1;
        var jumps = 0;
        var wireLength = 0;

        while (true)
        {
            if (position >= message.Length)
            {
                return false;
            }

            var length = message[position];
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length || ++jumps > MaxPointerJumps)
                {
                    return false;
                }

                var target = ((length & 0x3F) << 8) | message[position + 1];
                if (target >= position)
                {
                    return false;
                }

                if (end < 0)
                {
                    end = position + 2;
                }

                position = target;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                // 0x40 and 0x80 are the obsolete extended label types.
                return false;
            }

            wireLength += length + 1;
            if (wireLength > MaxNameWireLength)
            {
                return false;
            }

            if (length == 0)
            {
                position++;
                break;
            }

            if (position + 1 + length > message.Length)
            {
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            foreach (var b in message.Slice(position + 1, length))
            {
                if (b is > 0x20 and < 0x7F && b != (byte)'.' && b != (byte)'\\')
                {
                    builder.Append(char.ToLowerInvariant((char)b));
                }
                else
                {
                    builder.Append('\\').Append(b.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            position += 1 + length;
        }

        offset = end >= 0 ? end : position;
        name = builder.ToString();
        return true;
    }

    private static byte[] BuildReply(ReadOnlySpan<byte> answer, DnsQuery query, int responseCode, bool fromAnswer)
    {
        // The question bytes come from the answer only when they are there in full; otherwise a
        // bare header is still a valid "ask again over TCP".
        var questionLength = 0;
        if (answer.Length >= HeaderLength && BinaryPrimitives.ReadUInt16BigEndian(answer[4..]) >= 1)
        {
            var offset = HeaderLength;
            if (TryReadQuestion(answer, ref offset, out _))
            {
                questionLength = offset - HeaderLength;
            }
        }

        var reply = new byte[HeaderLength + questionLength];
        ushort flags = fromAnswer && answer.Length >= HeaderLength
            ? (ushort)(BinaryPrimitives.ReadUInt16BigEndian(answer[2..]) | FlagTruncated)
            : (ushort)(FlagResponse | FlagRecursionAvailable | FlagTruncated | (query.Flags & FlagRecursionDesired) | (responseCode & 0x0F));
        BinaryPrimitives.WriteUInt16BigEndian(reply, query.Id);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), (ushort)(questionLength > 0 ? 1 : 0));
        answer.Slice(HeaderLength, questionLength).CopyTo(reply.AsSpan(HeaderLength));
        return reply;
    }

    private static bool TryReadQuestion(ReadOnlySpan<byte> message, ref int offset, out DnsQuestion question)
    {
        question = default;
        var position = offset;
        if (!TryReadName(message, ref position, out var name) || position + 4 > message.Length)
        {
            return false;
        }

        question = new DnsQuestion(
            name,
            BinaryPrimitives.ReadUInt16BigEndian(message[position..]),
            BinaryPrimitives.ReadUInt16BigEndian(message[(position + 2)..]));
        offset = position + 4;
        return true;
    }

    private static bool TryReadRecord(ReadOnlySpan<byte> message, ref int offset, out ushort type, out ushort cls, out uint ttl, out (int Start, int Length) data)
    {
        type = 0;
        cls = 0;
        ttl = 0;
        data = default;

        var position = offset;
        if (!TryReadName(message, ref position, out _) || position + 10 > message.Length)
        {
            return false;
        }

        type = BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
        cls = BinaryPrimitives.ReadUInt16BigEndian(message[(position + 2)..]);
        ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(position + 4)..]);
        var length = BinaryPrimitives.ReadUInt16BigEndian(message[(position + 8)..]);
        position += 10;
        if (position + length > message.Length)
        {
            return false;
        }

        data = (position, length);
        offset = position + length;
        return true;
    }
}

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LabControl.Shared.Internet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// The whitelist's platform-neutral half (M6 portion 3, D-74): site patterns, the DNS wire
/// format, the learned-address and refused-name stores, and the loopback resolver end to end
/// against a fake upstream server on 127.0.0.1.
/// </summary>
public sealed class InternetResolverTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);

    // ---- HostPattern ----

    [Theory]
    [InlineData("docs.oracle.com", "docs.oracle.com")]
    [InlineData("  Docs.Oracle.COM  ", "docs.oracle.com")]
    [InlineData("https://docs.oracle.com/javase/17/", "docs.oracle.com")]
    [InlineData("docs.oracle.com/path?q=1#x", "docs.oracle.com")]
    [InlineData("http://x.example:8080", "x.example")]
    [InlineData("https://user:pw@site.example.org/", "site.example.org")]
    [InlineData("docs.oracle.com.", "docs.oracle.com")]
    [InlineData("*.jetbrains.com", "*.jetbrains.com")]
    [InlineData("*.JetBrains.com.", "*.jetbrains.com")]
    [InlineData("пример.укр", "xn--e1afmkfd.xn--j1amh")]
    [InlineData("*.пример.укр", "*.xn--e1afmkfd.xn--j1amh")]
    [InlineData("a-b.c1.example", "a-b.c1.example")]
    public void Entries_are_normalized_to_a_host(string input, string expected)
    {
        Assert.True(HostPattern.TryNormalize(input, out var pattern, out var problem), problem);
        Assert.Equal(expected, pattern);
        Assert.Equal(string.Empty, problem);
    }

    [Theory]
    [InlineData(null, HostPattern.Problems.Empty)]
    [InlineData("", HostPattern.Problems.Empty)]
    [InlineData("   ", HostPattern.Problems.Empty)]
    [InlineData("https:///", HostPattern.Problems.Empty)]
    [InlineData("*", HostPattern.Problems.WildcardTooBroad)]
    [InlineData("*.", HostPattern.Problems.WildcardTooBroad)]
    [InlineData("*.com", HostPattern.Problems.WildcardTooBroad)]
    [InlineData("docs.*.com", HostPattern.Problems.WildcardMisplaced)]
    [InlineData("*docs.com", HostPattern.Problems.WildcardMisplaced)]
    [InlineData("*.*.com", HostPattern.Problems.WildcardMisplaced)]
    [InlineData("192.168.1.10", HostPattern.Problems.IpLiteral)]
    [InlineData("http://10.0.0.1:8080/x", HostPattern.Problems.IpLiteral)]
    [InlineData("[::1]", HostPattern.Problems.IpLiteral)]
    [InlineData("http://[2001:db8::1]/", HostPattern.Problems.IpLiteral)]
    [InlineData("2001:db8::1", HostPattern.Problems.IpLiteral)]
    [InlineData("localhost", HostPattern.Problems.SingleLabel)]
    [InlineData("intranet", HostPattern.Problems.SingleLabel)]
    [InlineData("-bad.example", HostPattern.Problems.Invalid)]
    [InlineData("bad-.example", HostPattern.Problems.Invalid)]
    [InlineData("a..example", HostPattern.Problems.Invalid)]
    [InlineData("under_score.example", HostPattern.Problems.Invalid)]
    [InlineData("sp ace.example", HostPattern.Problems.Invalid)]
    [InlineData("x.example:port", HostPattern.Problems.Invalid)]
    [InlineData("1.2.3", HostPattern.Problems.Invalid)]
    public void Bad_entries_are_refused_with_a_code(string? input, string code)
    {
        Assert.False(HostPattern.TryNormalize(input, out var pattern, out var problem));
        Assert.Equal(string.Empty, pattern);
        Assert.Equal(code, problem);
    }

    [Fact]
    public void Over_long_labels_and_names_are_refused()
    {
        Assert.False(HostPattern.TryNormalize(new string('a', 64) + ".example", out _, out var label));
        Assert.Equal(HostPattern.Problems.Invalid, label);
        Assert.True(HostPattern.TryNormalize(new string('a', 63) + ".example", out _, out _));

        var longName = string.Join('.', Enumerable.Repeat(new string('a', 50), 5)) + ".example";
        Assert.False(HostPattern.TryNormalize(longName, out _, out var name));
        Assert.Equal(HostPattern.Problems.Invalid, name);
    }

    [Theory]
    [InlineData("docs.oracle.com", "docs.oracle.com", true)]
    [InlineData("docs.oracle.com", "DOCS.Oracle.com.", true)]
    [InlineData("docs.oracle.com", "www.docs.oracle.com", false)]
    [InlineData("docs.oracle.com", "oracle.com", false)]
    [InlineData("*.jetbrains.com", "jetbrains.com", true)]
    [InlineData("*.jetbrains.com", "jetbrains.com.", true)]
    [InlineData("*.jetbrains.com", "www.jetbrains.com", true)]
    [InlineData("*.jetbrains.com", "a.b.jetbrains.com", true)]
    [InlineData("*.jetbrains.com", "evILjetbrains.com", false)]
    [InlineData("*.jetbrains.com", "evil-jetbrains.com", false)]
    [InlineData("*.jetbrains.com", "jetbrains.com.evil", false)]
    [InlineData("*.jetbrains.com", "com", false)]
    [InlineData("*.jetbrains.com", "", false)]
    public void Patterns_match_the_site_and_never_a_lookalike(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, HostPattern.Matches(pattern, name));
    }

    [Fact]
    public void Any_pattern_of_the_list_may_match()
    {
        string[] patterns = ["docs.oracle.com", "*.jetbrains.com"];
        Assert.True(HostPattern.AnyMatches(patterns, "plugins.jetbrains.com"));
        Assert.True(HostPattern.AnyMatches(patterns, "docs.oracle.com"));
        Assert.False(HostPattern.AnyMatches(patterns, "oracle.com"));
        Assert.False(HostPattern.AnyMatches([], "docs.oracle.com"));
    }

    [Theory]
    [InlineData("WWW.Example.COM.", "www.example.com")]
    [InlineData("_ldap._tcp.dc.example", "_ldap._tcp.dc.example")]
    [InlineData("wpad", "wpad")]
    [InlineData("", null)]
    [InlineData(".", null)]
    [InlineData("a..b", null)]
    [InlineData("bad\\046name.example", null)]
    [InlineData("sp ace.example", null)]
    public void Query_names_are_normalized_or_rejected(string name, string? expected)
    {
        Assert.Equal(expected, HostPattern.NormalizeName(name));
    }

    // ---- DnsMessage ----

    [Fact]
    public void A_query_parses_and_our_replies_echo_it()
    {
        var query = Dns.Query(0xBEEF, "Docs.Oracle.com", DnsMessage.Types.A);
        Assert.True(DnsMessage.TryParseQuery(query, out var parsed));
        Assert.Equal(0xBEEF, parsed.Id);
        Assert.Equal(new DnsQuestion("docs.oracle.com", DnsMessage.Types.A, 1), parsed.Question);
        Assert.Equal(DnsMessage.ClassicUdpLimit, parsed.UdpPayloadLimit);

        foreach (var (reply, rcode) in new[]
                 {
                     (DnsMessage.BuildRefused(query, parsed), 5),
                     (DnsMessage.BuildNoData(query, parsed), 0),
                     (DnsMessage.BuildServerFailure(query, parsed), 2),
                 })
        {
            Assert.True(DnsMessage.TryParseResponse(reply, out var response));
            Assert.Equal(0xBEEF, response.Id);
            Assert.Equal(rcode, response.ResponseCode);
            Assert.False(response.Truncated);
            Assert.Equal(parsed.Question, response.Question);
            Assert.Empty(response.Addresses);

            var flags = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2));
            Assert.NotEqual(0, flags & 0x8000); // QR
            Assert.NotEqual(0, flags & 0x0100); // RD copied
            Assert.NotEqual(0, flags & 0x0080); // RA
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)));
        }
    }

    [Fact]
    public void The_edns_size_of_a_query_is_read()
    {
        var query = Dns.Query(1, "example.org", DnsMessage.Types.A, ednsSize: 1232);
        Assert.True(DnsMessage.TryParseQuery(query, out var parsed));
        Assert.Equal(1232, parsed.UdpPayloadLimit);
    }

    [Fact]
    public void Malformed_queries_are_refused_without_throwing()
    {
        var good = Dns.Query(7, "example.org", DnsMessage.Types.A);
        Assert.False(DnsMessage.TryParseQuery([], out _));
        Assert.False(DnsMessage.TryParseQuery(good.AsSpan(0, 11), out _));
        Assert.False(DnsMessage.TryParseQuery(good.AsSpan(0, good.Length - 1), out _));

        var response = good.ToArray();
        response[2] |= 0x80;
        Assert.False(DnsMessage.TryParseQuery(response, out _));

        var two = good.ToArray();
        two[5] = 2;
        Assert.False(DnsMessage.TryParseQuery(two, out _));

        // A pointer to itself in the question.
        var loop = new byte[12 + 2 + 4];
        loop[5] = 1;
        loop[12] = 0xC0;
        loop[13] = 12;
        Assert.False(DnsMessage.TryParseQuery(loop, out _));

        // A label running past the end.
        var past = good.ToArray();
        past[12] = 60;
        Assert.False(DnsMessage.TryParseQuery(past, out _));

        var random = new Random(42);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 80)];
            random.NextBytes(bytes);
            DnsMessage.TryParseQuery(bytes, out _);
            DnsMessage.TryParseResponse(bytes, out _);
        }
    }

    [Fact]
    public void A_compressed_answer_yields_every_address_with_its_ttl()
    {
        var query = Dns.Query(0x1234, "www.example.org", DnsMessage.Types.A);
        var answer = Dns.Answer(query,
            Dns.Cname("cdn.example.net", ttl: 30),
            Dns.A("192.0.2.10", ttl: 60, owner: Dns.Owner.CnameTarget),
            Dns.A("192.0.2.11", ttl: 61, owner: Dns.Owner.CnameTarget),
            Dns.Aaaa("2001:db8::10", ttl: 62, owner: Dns.Owner.CnameTarget));

        Assert.True(DnsMessage.TryParseResponse(answer, out var response));
        Assert.Equal(0x1234, response.Id);
        Assert.Equal(0, response.ResponseCode);
        Assert.Equal("www.example.org", response.Question!.Value.Name);
        Assert.Equal(
            [
                new DnsAddressRecord(IPAddress.Parse("192.0.2.10"), 60),
                new DnsAddressRecord(IPAddress.Parse("192.0.2.11"), 61),
                new DnsAddressRecord(IPAddress.Parse("2001:db8::10"), 62),
            ],
            response.Addresses);
    }

    [Fact]
    public void Response_names_survive_compression_and_hostile_pointers_fail()
    {
        var query = Dns.Query(1, "www.example.org", DnsMessage.Types.A);
        var answer = Dns.Answer(query, Dns.Cname("cdn.example.net", 30), Dns.A("192.0.2.1", 5, Dns.Owner.CnameTarget));

        // The CNAME's RDATA starts right after its fixed fields; read it as a name.
        var offset = 12;
        Assert.True(DnsMessage.TryReadName(answer, ref offset, out var questionName));
        Assert.Equal("www.example.org", questionName);

        // A forward pointer is refused.
        var forward = answer.ToArray();
        forward[query.Length] = 0xC0;
        forward[query.Length + 1] = (byte)(forward.Length - 2);
        Assert.False(DnsMessage.TryParseResponse(forward, out _));

        // A response cut short without TC is malformed; with TC what was read is kept.
        var cut = answer.AsSpan(0, answer.Length - 3).ToArray();
        Assert.False(DnsMessage.TryParseResponse(cut, out _));
        cut[2] |= 0x02;
        Assert.True(DnsMessage.TryParseResponse(cut, out var truncated));
        Assert.True(truncated.Truncated);
        Assert.Empty(truncated.Addresses);
    }

    [Fact]
    public void A_truncated_stand_in_keeps_the_question_and_sets_tc()
    {
        var query = Dns.Query(0x4242, "big.example.org", DnsMessage.Types.A);
        Assert.True(DnsMessage.TryParseQuery(query, out var parsed));
        var answer = Dns.Answer(query, Enumerable.Range(1, 60).Select(i => Dns.A($"192.0.2.{i}", 60)).ToArray());
        Assert.True(answer.Length > DnsMessage.ClassicUdpLimit);

        var stand = DnsMessage.BuildTruncated(answer, parsed);
        Assert.True(DnsMessage.TryParseResponse(stand, out var response));
        Assert.True(response.Truncated);
        Assert.Equal(0x4242, response.Id);
        Assert.Equal(parsed.Question, response.Question);
        Assert.Empty(response.Addresses);
    }

    // ---- AllowedAddressSet ----

    [Fact]
    public void The_address_set_keeps_the_most_recent_up_to_its_cap()
    {
        var set = new AllowedAddressSet(cap: 3);
        Assert.True(set.Admit([IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2")]));
        Assert.False(set.Admit([IPAddress.Parse("192.0.2.1")])); // refresh only
        Assert.True(set.Admit([IPAddress.Parse("192.0.2.3"), IPAddress.Parse("192.0.2.4")]));

        // .2 was the least recently admitted, so it made room.
        Assert.Equal(
            ["192.0.2.1", "192.0.2.3", "192.0.2.4"],
            set.Snapshot().Select(a => a.ToString()));
        Assert.Equal(3, set.Count);

        set.Clear();
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Mapped_addresses_are_kept_as_ipv4_and_nonsense_is_skipped()
    {
        var set = new AllowedAddressSet();
        Assert.True(set.Admit([IPAddress.Parse("::ffff:192.0.2.9")]));
        Assert.False(set.Admit([IPAddress.Parse("192.0.2.9")]));
        Assert.False(set.Admit(
        [
            IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Any, IPAddress.IPv6Any,
            IPAddress.Broadcast, IPAddress.Parse("224.0.0.251"), IPAddress.Parse("ff02::fb"),
            IPAddress.Parse("127.0.0.53"),
        ]));
        Assert.Equal([IPAddress.Parse("192.0.2.9")], set.Snapshot());
        Assert.True(set.Admit([IPAddress.Parse("2001:db8::1")]));
        Assert.Equal(2, set.Count);
    }

    // ---- RefusedNames ----

    [Fact]
    public void Refused_names_are_distinct_most_recent_first_and_versioned()
    {
        var start = DateTimeOffset.Parse("2026-10-17T10:00:00Z");
        var names = new RefusedNames(TimeSpan.FromMinutes(10), max: 3);
        var v0 = names.Version;

        names.Note("a.example", start);
        names.Note("b.example", start.AddSeconds(1));
        var v1 = names.Version;
        Assert.True(v1 > v0);

        names.Note("b.example", start.AddSeconds(2)); // already first: no change
        Assert.Equal(v1, names.Version);

        names.Note("a.example", start.AddSeconds(3)); // moves to the front
        Assert.True(names.Version > v1);
        Assert.Equal(["a.example", "b.example"], names.Recent(start.AddSeconds(3)));

        names.Note("c.example", start.AddSeconds(4));
        names.Note("d.example", start.AddSeconds(5));
        Assert.Equal(["d.example", "c.example", "a.example"], names.Recent(start.AddSeconds(5)));

        // a was refused at +3 s; ten minutes on it is gone, and the pruning moves the version.
        var before = names.Version;
        Assert.Equal(["d.example", "c.example"], names.Recent(start.AddSeconds(3).AddMinutes(10).AddSeconds(1)));
        Assert.True(names.Version > before);

        names.Clear();
        Assert.Empty(names.Recent(start));
    }

    // ---- LoopbackResolver ----

    [Fact]
    public async Task An_allowed_name_is_admitted_before_the_answer_arrives()
    {
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, Dns.Cname("edge.cdn.example", 60), Dns.A("192.0.2.80", 60, Dns.Owner.CnameTarget), Dns.Aaaa("2001:db8::80", 60, Dns.Owner.CnameTarget)));
        var admitted = new ConcurrentQueue<IPAddress[]>();
        await using var resolver = StartResolver([upstream.Endpoint], ["*.oracle.com"], a => { admitted.Enqueue([.. a]); return true; });

        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(0x0A0B, "docs.oracle.com", DnsMessage.Types.A));

        Assert.NotNull(reply);
        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.Equal(0x0A0B, response.Id);
        Assert.Equal(0, response.ResponseCode);
        Assert.Equal(2, response.Addresses.Count);
        Assert.True(admitted.TryDequeue(out var addresses)); // recorded before the reply was sent
        Assert.Equal([IPAddress.Parse("192.0.2.80"), IPAddress.Parse("2001:db8::80")], addresses);
        Assert.Empty(resolver.Refused.Recent(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_refused_admission_answers_servfail()
    {
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.81", 60)));
        await using var resolver = StartResolver([upstream.Endpoint], ["docs.oracle.com"], _ => false);

        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(5, "docs.oracle.com", DnsMessage.Types.A));

        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.Equal(DnsMessage.ResponseCodes.ServerFailure, response.ResponseCode);
        Assert.Empty(response.Addresses);
    }

    [Fact]
    public async Task An_unlisted_name_is_refused_and_noted_but_noise_is_not_noted()
    {
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.82", 60)));
        var admits = 0;
        await using var resolver = StartResolver([upstream.Endpoint], ["docs.oracle.com"], _ => { Interlocked.Increment(ref admits); return true; });
        var endpoint = resolver.BoundEndpoints[0];

        foreach (var name in new[] { "www.youtube.com", "intranet", "wpad", "wpad.corp.example", "printer.local", "nas.lan", "box.home.arpa", "www.msftconnecttest.com", "dns.msftncsi.com", "evildocs.oracle.com" })
        {
            var reply = await Client.UdpAsync(endpoint, Dns.Query(9, name, DnsMessage.Types.A));
            Assert.True(DnsMessage.TryParseResponse(reply, out var response));
            Assert.Equal(DnsMessage.ResponseCodes.Refused, response.ResponseCode);
        }

        Assert.Equal(["evildocs.oracle.com", "www.youtube.com"], resolver.Refused.Recent(DateTimeOffset.UtcNow));
        Assert.Equal(0, upstream.UdpQueries);
        Assert.Equal(0, admits);
    }

    [Fact]
    public async Task Https_and_svcb_for_an_allowed_name_get_an_empty_answer()
    {
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.83", 60)));
        await using var resolver = StartResolver([upstream.Endpoint], ["*.jetbrains.com"], _ => true);

        foreach (var type in new[] { DnsMessage.Types.Https, DnsMessage.Types.Svcb })
        {
            var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(11, "www.jetbrains.com", type));
            Assert.True(DnsMessage.TryParseResponse(reply, out var response));
            Assert.Equal(DnsMessage.ResponseCodes.NoError, response.ResponseCode);
            Assert.Equal(type, response.Question!.Value.Type);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)));
        }

        Assert.Equal(0, upstream.UdpQueries);
    }

    [Fact]
    public async Task A_truncated_upstream_answer_is_asked_again_over_tcp()
    {
        await using var upstream = FakeUpstream.Start(
            q => Dns.Answer(q, Dns.A("192.0.2.84", 60)),
            udp: q => Dns.Answer(q, truncated: true));
        var admitted = new ConcurrentQueue<IPAddress>();
        await using var resolver = StartResolver([upstream.Endpoint], ["docs.oracle.com"], a => { foreach (var x in a) admitted.Enqueue(x); return true; });

        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(12, "docs.oracle.com", DnsMessage.Types.A));

        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.False(response.Truncated);
        Assert.Equal([IPAddress.Parse("192.0.2.84")], response.Addresses.Select(a => a.Address));
        Assert.Equal(1, upstream.UdpQueries);
        Assert.Equal(1, upstream.TcpQueries);
        Assert.Equal([IPAddress.Parse("192.0.2.84")], admitted);
    }

    [Fact]
    public async Task An_answer_too_big_for_the_udp_client_is_admitted_and_truncated_for_it()
    {
        var many = Enumerable.Range(1, 60).Select(i => Dns.A($"198.51.100.{i}", 60)).ToArray();
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, many), udp: q => Dns.Answer(q, truncated: true));
        var admittedCount = 0;
        await using var resolver = StartResolver([upstream.Endpoint], ["big.example.org"], a => { admittedCount = a.Count; return true; });

        var udpReply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(13, "big.example.org", DnsMessage.Types.A));
        Assert.True(DnsMessage.TryParseResponse(udpReply, out var udpResponse));
        Assert.True(udpResponse.Truncated);
        Assert.Equal(60, admittedCount);

        var tcpReply = await Client.TcpAsync(resolver.BoundEndpoints[0], Dns.Query(14, "big.example.org", DnsMessage.Types.A));
        Assert.True(DnsMessage.TryParseResponse(tcpReply[0], out var tcpResponse));
        Assert.Equal(14, tcpResponse.Id);
        Assert.Equal(60, tcpResponse.Addresses.Count);
    }

    [Fact]
    public async Task A_silent_first_upstream_falls_through_to_the_second_in_time()
    {
        await using var silent = FakeUpstream.Start(_ => null);
        await using var failing = FakeUpstream.Start(q => Dns.Answer(q, rcode: DnsMessage.ResponseCodes.ServerFailure));
        await using var working = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.85", 60)));
        await using var resolver = StartResolver([silent.Endpoint, failing.Endpoint, working.Endpoint], ["docs.oracle.com"], _ => true);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(15, "docs.oracle.com", DnsMessage.Types.A));

        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.Equal([IPAddress.Parse("192.0.2.85")], response.Addresses.Select(a => a.Address));
        Assert.Equal(1, silent.UdpQueries);
        Assert.Equal(1, failing.UdpQueries);
        Assert.True(started.Elapsed < ShortTimeout * 4, $"took {started.Elapsed}");
    }

    [Fact]
    public async Task No_upstream_answering_is_servfail()
    {
        await using var silent = FakeUpstream.Start(_ => null);
        await using var resolver = StartResolver([silent.Endpoint], ["docs.oracle.com"], _ => true);

        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(16, "docs.oracle.com", DnsMessage.Types.A));
        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.Equal(DnsMessage.ResponseCodes.ServerFailure, response.ResponseCode);
    }

    [Fact]
    public async Task An_answer_with_the_wrong_id_or_question_is_ignored()
    {
        await using var spoofing = FakeUpstream.Start(q =>
        {
            var answer = Dns.Answer(q, Dns.A("203.0.113.66", 60));
            answer[0] ^= 0xFF;
            return answer;
        });
        await using var resolver = StartResolver([spoofing.Endpoint], ["docs.oracle.com"], _ => throw new InvalidOperationException("never admitted"));

        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(17, "docs.oracle.com", DnsMessage.Types.A));
        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.Equal(DnsMessage.ResponseCodes.ServerFailure, response.ResponseCode);
    }

    [Fact]
    public async Task The_resolver_never_asks_itself_upstream()
    {
        await using var working = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.90", 60)));
        await using var resolver = StartResolver([working.Endpoint], ["docs.oracle.com"], _ => true);
        resolver.SetUpstream([resolver.BoundEndpoints[0], working.Endpoint]);

        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(42, "docs.oracle.com", DnsMessage.Types.A));
        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.Equal([IPAddress.Parse("192.0.2.90")], response.Addresses.Select(a => a.Address));
    }

    [Fact]
    public async Task Nxdomain_for_an_allowed_name_is_relayed()
    {
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, rcode: DnsMessage.ResponseCodes.NameError));
        await using var resolver = StartResolver([upstream.Endpoint], ["*.oracle.com"], _ => true);

        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(18, "nope.oracle.com", DnsMessage.Types.A));
        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.Equal(DnsMessage.ResponseCodes.NameError, response.ResponseCode);
    }

    [Fact]
    public async Task Reverse_lookups_are_forwarded_and_never_admitted_or_noted()
    {
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, Dns.Ptr("router.lan", 60)));
        var admits = 0;
        await using var resolver = StartResolver([upstream.Endpoint], ["docs.oracle.com"], _ => { admits++; return true; });

        var reply = await Client.UdpAsync(resolver.BoundEndpoints[0], Dns.Query(19, "1.1.168.192.in-addr.arpa", DnsMessage.Types.Ptr));

        Assert.True(DnsMessage.TryParseResponse(reply, out var response));
        Assert.Equal(19, response.Id);
        Assert.Equal(0, response.ResponseCode);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)));
        Assert.Equal(1, upstream.UdpQueries);
        Assert.Equal(0, admits);
        Assert.Empty(resolver.Refused.Recent(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_tcp_client_may_ask_several_questions_on_one_connection()
    {
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.86", 60)));
        await using var resolver = StartResolver([upstream.Endpoint], ["docs.oracle.com"], _ => true);

        var replies = await Client.TcpAsync(resolver.BoundEndpoints[0],
            Dns.Query(21, "docs.oracle.com", DnsMessage.Types.A),
            Dns.Query(22, "www.youtube.com", DnsMessage.Types.A));

        Assert.True(DnsMessage.TryParseResponse(replies[0], out var first));
        Assert.Equal(21, first.Id);
        Assert.Equal([IPAddress.Parse("192.0.2.86")], first.Addresses.Select(a => a.Address));
        Assert.True(DnsMessage.TryParseResponse(replies[1], out var second));
        Assert.Equal(22, second.Id);
        Assert.Equal(DnsMessage.ResponseCodes.Refused, second.ResponseCode);
    }

    [Fact]
    public async Task The_whitelist_and_upstream_change_live()
    {
        await using var first = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.87", 60)));
        await using var second = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.88", 60)));
        await using var resolver = StartResolver([first.Endpoint], ["docs.oracle.com"], _ => true);
        var endpoint = resolver.BoundEndpoints[0];

        Assert.True(DnsMessage.TryParseResponse(await Client.UdpAsync(endpoint, Dns.Query(31, "www.jetbrains.com", DnsMessage.Types.A)), out var before));
        Assert.Equal(DnsMessage.ResponseCodes.Refused, before.ResponseCode);

        resolver.SetAllowed(["*.jetbrains.com"]);
        resolver.SetUpstream([second.Endpoint]);

        Assert.True(DnsMessage.TryParseResponse(await Client.UdpAsync(endpoint, Dns.Query(32, "www.jetbrains.com", DnsMessage.Types.A)), out var after));
        Assert.Equal([IPAddress.Parse("192.0.2.88")], after.Addresses.Select(a => a.Address));
        Assert.True(DnsMessage.TryParseResponse(await Client.UdpAsync(endpoint, Dns.Query(33, "docs.oracle.com", DnsMessage.Types.A)), out var dropped));
        Assert.Equal(DnsMessage.ResponseCodes.Refused, dropped.ResponseCode);
        Assert.Equal(0, first.UdpQueries);
    }

    [Fact]
    public async Task Malformed_queries_are_dropped_and_the_resolver_keeps_answering()
    {
        await using var upstream = FakeUpstream.Start(q => Dns.Answer(q, Dns.A("192.0.2.89", 60)));
        await using var resolver = StartResolver([upstream.Endpoint], ["docs.oracle.com"], _ => true);
        var endpoint = resolver.BoundEndpoints[0];

        Assert.Null(await Client.UdpAsync(endpoint, [1, 2, 3], TimeSpan.FromMilliseconds(300)));
        var asResponse = Dns.Query(40, "docs.oracle.com", DnsMessage.Types.A);
        asResponse[2] |= 0x80;
        Assert.Null(await Client.UdpAsync(endpoint, asResponse, TimeSpan.FromMilliseconds(300)));

        Assert.True(DnsMessage.TryParseResponse(await Client.UdpAsync(endpoint, Dns.Query(41, "docs.oracle.com", DnsMessage.Types.A)), out var response));
        Assert.Equal(41, response.Id);
        Assert.True(resolver.Running);
    }

    [Fact]
    public async Task A_taken_port_fails_the_start_with_a_code_and_binds_nothing()
    {
        using var squatter = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        squatter.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var taken = (IPEndPoint)squatter.LocalEndPoint!;

        await using var resolver = new LoopbackResolver(new LoopbackResolverOptions(
            [new IPEndPoint(IPAddress.Loopback, 0), taken], [], [], _ => true, NullLogger.Instance));

        Assert.False(resolver.TryStart(out var problem));
        Assert.Equal(LoopbackResolver.Problems.PortInUse, problem);
        Assert.False(resolver.Running);
        Assert.Empty(resolver.BoundEndpoints);
    }

    [Fact]
    public async Task Start_twice_and_after_dispose_are_refused()
    {
        var resolver = StartResolver([], [], _ => true);
        Assert.False(resolver.TryStart(out var again));
        Assert.Equal(LoopbackResolver.Problems.AlreadyStarted, again);

        await resolver.DisposeAsync();
        Assert.False(resolver.Running);
        Assert.False(resolver.TryStart(out var disposed));
        Assert.Equal(LoopbackResolver.Problems.Disposed, disposed);
        await resolver.DisposeAsync();
    }

    private static LoopbackResolver StartResolver(IReadOnlyList<IPEndPoint> upstream, IReadOnlyList<string> allowed, Func<IReadOnlyList<IPAddress>, bool> admit)
    {
        var resolver = new LoopbackResolver(new LoopbackResolverOptions(
            [new IPEndPoint(IPAddress.Loopback, 0)], upstream, allowed, admit, NullLogger.Instance)
        {
            UpstreamTimeout = ShortTimeout,
        });
        Assert.True(resolver.TryStart(out var problem), problem);
        Assert.NotEqual(0, resolver.BoundEndpoints[0].Port);
        return resolver;
    }

    /// <summary>A stub client asking the resolver as Windows would.</summary>
    private static class Client
    {
        public static async Task<byte[]?> UdpAsync(IPEndPoint server, byte[] query, TimeSpan? timeout = null)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(server);
            await socket.SendAsync(query, SocketFlags.None);
            using var deadline = new CancellationTokenSource(timeout ?? ClientTimeout);
            var buffer = new byte[65535];
            try
            {
                var received = await socket.ReceiveAsync(buffer, SocketFlags.None, deadline.Token);
                return buffer.AsSpan(0, received).ToArray();
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public static async Task<byte[][]> TcpAsync(IPEndPoint server, params byte[][] queries)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(server);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var deadline = new CancellationTokenSource(ClientTimeout);
            var replies = new List<byte[]>();
            foreach (var query in queries)
            {
                await Dns.WriteFrameAsync(stream, query, deadline.Token);
                replies.Add(await Dns.ReadFrameAsync(stream, deadline.Token) ?? throw new IOException("no reply"));
            }

            return [.. replies];
        }
    }

    /// <summary>
    /// A DNS server on 127.0.0.1 (UDP and TCP on one ephemeral port) that answers through a
    /// handler; <c>null</c> from the handler means silence.
    /// </summary>
    private sealed class FakeUpstream : IAsyncDisposable
    {
        private readonly Socket _udp;
        private readonly Socket _tcp;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task[] _loops;
        private readonly Func<byte[], byte[]?> _tcpHandler;
        private readonly Func<byte[], byte[]?> _udpHandler;
        private int _udpQueries;
        private int _tcpQueries;

        private FakeUpstream(Func<byte[], byte[]?> handler, Func<byte[], byte[]?>? udp)
        {
            _tcpHandler = handler;
            _udpHandler = udp ?? handler;
            _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Endpoint = (IPEndPoint)_udp.LocalEndPoint!;
            _tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _tcp.Bind(Endpoint);
            _tcp.Listen(16);
            _loops = [Task.Run(UdpLoopAsync), Task.Run(TcpLoopAsync)];
        }

        public IPEndPoint Endpoint { get; }

        public int UdpQueries => Volatile.Read(ref _udpQueries);

        public int TcpQueries => Volatile.Read(ref _tcpQueries);

        public static FakeUpstream Start(Func<byte[], byte[]?> handler, Func<byte[], byte[]?>? udp = null)
        {
            // An ephemeral UDP port whose TCP twin is taken is rare; try again then.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return new FakeUpstream(handler, udp);
                }
                catch (SocketException) when (attempt < 5)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _udp.Dispose();
            _tcp.Dispose();
            try
            {
                await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        private async Task UdpLoopAsync()
        {
            var buffer = new byte[65535];
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var received = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                    Interlocked.Increment(ref _udpQueries);
                    var reply = _udpHandler(buffer.AsSpan(0, received.ReceivedBytes).ToArray());
                    if (reply is not null)
                    {
                        await _udp.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint);
                    }
                }
                catch when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException)
                {
                }
            }
        }

        private async Task TcpLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _tcp.AcceptAsync(_stop.Token);
                    await using var stream = new NetworkStream(client, ownsSocket: false);
                    while (await Dns.ReadFrameAsync(stream, _stop.Token) is { } query)
                    {
                        Interlocked.Increment(ref _tcpQueries);
                        if (_tcpHandler(query) is { } reply)
                        {
                            await Dns.WriteFrameAsync(stream, reply, _stop.Token);
                        }
                    }
                }
                catch when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                }
            }
        }
    }

    /// <summary>Builds DNS messages the way real clients and servers do, compression included.</summary>
    private static class Dns
    {
        public enum Owner
        {
            /// <summary>The question's name, by a pointer to offset 12.</summary>
            Question,

            /// <summary>The name in the RDATA of the most recent CNAME, by a pointer to it.</summary>
            CnameTarget,
        }

        public sealed record Record(ushort Type, Owner Owner, uint Ttl, byte[]? Data, string? NameData);

        public static Record A(string address, uint ttl, Owner owner = Owner.Question) =>
            new(DnsMessage.Types.A, owner, ttl, IPAddress.Parse(address).GetAddressBytes(), null);

        public static Record Aaaa(string address, uint ttl, Owner owner = Owner.Question) =>
            new(DnsMessage.Types.Aaaa, owner, ttl, IPAddress.Parse(address).GetAddressBytes(), null);

        public static Record Cname(string target, uint ttl) => new(DnsMessage.Types.Cname, Owner.Question, ttl, null, target);

        public static Record Ptr(string target, uint ttl) => new(DnsMessage.Types.Ptr, Owner.Question, ttl, null, target);

        public static byte[] Query(ushort id, string name, ushort type, int? ednsSize = null)
        {
            var bytes = new List<byte>();
            Write16(bytes, id);
            Write16(bytes, 0x0100); // RD
            Write16(bytes, 1);
            Write16(bytes, 0);
            Write16(bytes, 0);
            Write16(bytes, (ushort)(ednsSize is null ? 0 : 1));
            WriteName(bytes, name);
            Write16(bytes, type);
            Write16(bytes, 1);
            if (ednsSize is { } size)
            {
                bytes.Add(0); // root
                Write16(bytes, DnsMessage.Types.Opt);
                Write16(bytes, (ushort)size);
                Write32(bytes, 0);
                Write16(bytes, 0);
            }

            return [.. bytes];
        }

        /// <summary>An answer to <paramref name="query"/>: header, its question, then the records.</summary>
        public static byte[] Answer(byte[] query, params Record[] records) => Answer(query, false, 0, records);

        public static byte[] Answer(byte[] query, bool truncated = false, int rcode = 0, params Record[] records)
        {
            Assert.True(DnsMessage.TryParseQuery(query, out var parsed));
            var bytes = new List<byte>();
            Write16(bytes, parsed.Id);
            Write16(bytes, (ushort)(0x8000 | 0x0100 | 0x0080 | (truncated ? 0x0200 : 0) | rcode));
            Write16(bytes, 1);
            Write16(bytes, (ushort)records.Length);
            Write16(bytes, 0);
            Write16(bytes, 0);
            bytes.AddRange(query.AsSpan(12, parsed.QuestionEnd - 12).ToArray());

            var cnameTarget = -1;
            foreach (var record in records)
            {
                var owner = record.Owner == Owner.CnameTarget ? cnameTarget : 12;
                Assert.True(owner >= 0);
                Write16(bytes, (ushort)(0xC000 | owner));
                Write16(bytes, record.Type);
                Write16(bytes, 1);
                Write32(bytes, record.Ttl);
                if (record.NameData is { } target)
                {
                    var name = new List<byte>();
                    WriteName(name, target);
                    Write16(bytes, (ushort)name.Count);
                    if (record.Type == DnsMessage.Types.Cname)
                    {
                        cnameTarget = bytes.Count;
                    }

                    bytes.AddRange(name);
                }
                else
                {
                    Write16(bytes, (ushort)record.Data!.Length);
                    bytes.AddRange(record.Data);
                }
            }

            return [.. bytes];
        }

        public static async Task WriteFrameAsync(Stream stream, byte[] message, CancellationToken ct)
        {
            var framed = new byte[message.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)message.Length);
            message.CopyTo(framed, 2);
            await stream.WriteAsync(framed, ct);
        }

        public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
        {
            var prefix = new byte[2];
            if (await stream.ReadAtLeastAsync(prefix, 2, throwOnEndOfStream: false, ct) < 2)
            {
                return null;
            }

            var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
            return await stream.ReadAtLeastAsync(message, message.Length, throwOnEndOfStream: false, ct) < message.Length ? null : message;
        }

        private static void WriteName(List<byte> bytes, string name)
        {
            foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var ascii = Encoding.ASCII.GetBytes(label);
                bytes.Add((byte)ascii.Length);
                bytes.AddRange(ascii);
            }

            bytes.Add(0);
        }

        private static void Write16(List<byte> bytes, ushort value)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        private static void Write32(List<byte> bytes, uint value)
        {
            Write16(bytes, (ushort)(value >> 16));
            Write16(bytes, (ushort)value);
        }
    }
}

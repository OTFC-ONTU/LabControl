using System.IO.Pipes;
using LabControl.Shared.Protocol;
using LabControl.Shared.Session;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// The agent ↔ helper pipe framing (PROTOCOL.md, "Agent ↔ Session helper"). Nothing in it
/// is Windows-specific, so it is proven here over a real named-pipe pair on any OS.
/// </summary>
public class PipeFramingTests
{
    [Fact]
    public async Task Messages_round_trip_in_order_over_a_named_pipe()
    {
        var name = "lc-" + Guid.NewGuid().ToString("n")[..8];
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var token = TestContext.Current.CancellationToken;

        await Task.WhenAll(server.WaitForConnectionAsync(token), client.ConnectAsync(token));

        await PipeFraming.WriteAsync(client, new HelperMessage { Hello = new HelperHello { SessionId = 1, ProcessId = 4242, Version = "0.1.0" } }, token);
        await PipeFraming.WriteAsync(client, new HelperMessage { Status = new HelperStatus { InputDesktop = "Default", ScreenWidth = 1920, ScreenHeight = 1080 } }, token);
        await PipeFraming.WriteAsync(server, new ServiceMessage { Ping = new Ping { SentAtUnix = 123 } }, token);

        var hello = await PipeFraming.ReadAsync(server, HelperMessage.Parser, token);
        var status = await PipeFraming.ReadAsync(server, HelperMessage.Parser, token);
        var ping = await PipeFraming.ReadAsync(client, ServiceMessage.Parser, token);

        Assert.Equal(HelperMessage.PayloadOneofCase.Hello, hello!.PayloadCase);
        Assert.Equal(4242u, hello.Hello.ProcessId);
        Assert.Equal("Default", status!.Status.InputDesktop);
        Assert.Equal(1920, status.Status.ScreenWidth);
        Assert.Equal(123, ping!.Ping.SentAtUnix);

        // A clean close between frames reads as "no more messages", not as an error.
        await client.DisposeAsync();
        Assert.Null(await PipeFraming.ReadAsync(server, HelperMessage.Parser, token));
    }

    [Fact]
    public async Task A_frame_larger_than_the_limit_is_refused_on_both_sides()
    {
        var token = TestContext.Current.CancellationToken;
        var big = new HelperMessage { Status = new HelperStatus { InputDesktop = new string('x', 100) } };

        await using var sink = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.WriteAsync(sink, big, token, maxBytes: 16).AsTask());

        await using var stream = new MemoryStream();
        await PipeFraming.WriteAsync(stream, big, token);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.ReadAsync(stream, HelperMessage.Parser, token, maxBytes: 16).AsTask());
    }

    [Fact]
    public async Task A_pipe_that_closes_mid_frame_is_a_protocol_error_not_a_message()
    {
        var token = TestContext.Current.CancellationToken;
        await using var whole = new MemoryStream();
        await PipeFraming.WriteAsync(whole, new HelperMessage { Hello = new HelperHello { Version = "0.1.0", ProcessId = 7 } }, token);

        var bytes = whole.ToArray();
        await using var headerOnly = new MemoryStream(bytes, 0, PipeFraming.HeaderBytes + 2);
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeFraming.ReadAsync(headerOnly, HelperMessage.Parser, token).AsTask());

        await using var halfHeader = new MemoryStream(bytes, 0, 2);
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeFraming.ReadAsync(halfHeader, HelperMessage.Parser, token).AsTask());
    }
}

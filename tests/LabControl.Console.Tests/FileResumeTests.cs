using Grpc.Core;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

public sealed class FileResumeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Defaults.FileChunkBytes)]
    [InlineData(Defaults.FileChunkBytes * 2)]
    public async Task Empty_and_exact_chunk_sized_files_finish_with_a_verified_terminal_chunk(int size)
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.IsLinked));
        var content = new byte[size];
        Random.Shared.NextBytes(content);
        var offer = console.Session.Files.OfferBytes(content, "boundary.bin");
        using var destination = new MemoryStream();
        Assert.Equal(size, await pc.Link.PullFileAsync(offer.Reference, offer.Sha256, destination, TestContext.Current.CancellationToken));
        Assert.Equal(content, destination.ToArray());
    }

    [Theory]
    [InlineData(123)]
    [InlineData(Defaults.FileChunkBytes)]
    [InlineData(Defaults.FileChunkBytes * 2)]
    public async Task The_console_serves_the_suffix_and_reports_the_full_file_size(int offset)
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.InstallEnrolled(console, 1, TimeSpan.FromDays(365));
        var content = new byte[Defaults.FileChunkBytes * 2];
        Random.Shared.NextBytes(content);
        var offer = console.Session.Files.OfferBytes(content, "suffix.bin");
        var writer = new ChunkWriter();
        await console.Session.ServeFileAsync(pc.Store.Certificate,
            new FileRequest { Reference = offer.Reference, Offset = offset }, writer, TestContext.Current.CancellationToken);
        Assert.Equal(offset, writer.Chunks[0].Offset);
        Assert.Equal(content[offset..], writer.Chunks.SelectMany(c => c.Data.ToByteArray()).ToArray());
        Assert.True(writer.Chunks[^1].Last);
        Assert.Equal(content.Length, writer.Chunks[^1].TotalBytes);
        Assert.Equal(offer.Sha256, writer.Chunks[^1].Sha256);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task An_offset_outside_the_file_is_refused_before_any_bytes_are_sent(int offset)
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.InstallEnrolled(console, 1, TimeSpan.FromDays(365));
        var offer = console.Session.Files.OfferBytes([1, 2, 3], "small.bin");
        var writer = new ChunkWriter();
        var error = await Assert.ThrowsAsync<RpcException>(() => console.Session.ServeFileAsync(pc.Store.Certificate,
            new FileRequest { Reference = offer.Reference, Offset = offset }, writer, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.OutOfRange, error.StatusCode);
        Assert.Empty(writer.Chunks);
    }

    [Fact]
    public async Task A_pull_resumes_after_two_link_breaks_without_repeating_destination_bytes()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.IsLinked));
        var content = new byte[Defaults.FileChunkBytes * 8 + 123];
        Random.Shared.NextBytes(content);
        var offer = console.Session.Files.OfferBytes(content, "resume.bin");
        var links = 0;
        pc.Link.Linked += (_, _) => Interlocked.Increment(ref links);
        using var destination = new InterruptingStream(async () =>
        {
            pc.Link.Disconnect("file transfer reconnect test");
            Assert.True(await Wait.UntilAsync(() => !pc.Link.IsLinked));
        }, interruptions: 2);
        Assert.Equal(content.Length, await pc.Link.PullFileAsync(offer.Reference, offer.Sha256, destination, TestContext.Current.CancellationToken));
        Assert.Equal(content, destination.ToArray());
        Assert.True(Volatile.Read(ref links) >= 2);
    }

    [Fact]
    public async Task Changed_content_after_a_reconnect_fails_the_whole_file_hash()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.IsLinked));
        var content = new byte[Defaults.FileChunkBytes * 8];
        var offer = console.Session.Files.OfferBytes(content, "changed.bin");
        using var destination = new InterruptingStream(async () =>
        {
            pc.Link.Disconnect("change source between attempts");
            content[^1] = 1;
            Assert.True(await Wait.UntilAsync(() => !pc.Link.IsLinked));
        }, interruptions: 1);
        var error = await Assert.ThrowsAsync<FilePullException>(() =>
            pc.Link.PullFileAsync(offer.Reference, offer.Sha256, destination, TestContext.Current.CancellationToken));
        Assert.Contains("hash", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_resized_disk_offer_is_refused_before_writing_the_destination()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.IsLinked));
        var path = Path.Combine(console.Directory, "changed.bin");
        await File.WriteAllBytesAsync(path, new byte[100], TestContext.Current.CancellationToken);
        var offer = console.Session.Files.OfferFile(path);
        await File.WriteAllBytesAsync(path, new byte[101], TestContext.Current.CancellationToken);
        using var destination = new MemoryStream();
        var error = await Assert.ThrowsAsync<FilePullException>(() =>
            pc.Link.PullFileAsync(offer.Reference, offer.Sha256, destination, TestContext.Current.CancellationToken));
        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task Cancellation_during_a_reconnect_does_not_wait_for_the_inactivity_timeout()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.IsLinked));
        var offer = console.Session.Files.OfferBytes(new byte[Defaults.FileChunkBytes * 8], "cancel.bin");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var destination = new InterruptingStream(() =>
        {
            pc.Link.Disconnect("cancel transfer");
            cancellation.Cancel();
            return Task.CompletedTask;
        }, interruptions: 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pc.Link.PullFileAsync(offer.Reference, offer.Sha256, destination, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(Defaults.FileChunkBytes, destination.Length);
    }

    private sealed class ChunkWriter : IServerStreamWriter<FileChunk>
    {
        public List<FileChunk> Chunks { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(FileChunk message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task WriteAsync(FileChunk message)
        {
            Chunks.Add(message.Clone());
            return Task.CompletedTask;
        }
    }

    // Append-only on purpose: reconnects must never seek or rewrite earlier chunks.
    private sealed class InterruptingStream(Func<Task> interrupt, int interruptions) : MemoryStream
    {
        private int _remaining = interruptions;
        public override bool CanSeek => false;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (_remaining-- > 0)
            {
                await interrupt();
            }
        }
    }
}

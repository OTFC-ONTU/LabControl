using Google.Protobuf;
using Grpc.Core;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

public sealed class FileUploadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Defaults.FileChunkBytes)]
    [InlineData(Defaults.FileChunkBytes * 2 + 123)]
    public async Task Uploads_are_verified_over_TLS_and_completed_uploads_are_idempotent(int size)
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.IsLinked));
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        var hash = FileHash.Sha256Hex(bytes);
        await using var grant = console.Session.Uploads.Expect(pc.AgentId, destination, size, hash);
        Assert.Equal(size, await pc.Link.PushFileAsync(grant.Reference, hash, source, TestContext.Current.CancellationToken));
        Assert.Equal(bytes, destination.ToArray());
        await Assert.ThrowsAsync<FilePushException>(() => pc.Link.PushFileAsync(grant.Reference,
            new string('0', 64), source, TestContext.Current.CancellationToken));
        Assert.Equal(size, await pc.Link.PushFileAsync(grant.Reference, hash, source, TestContext.Current.CancellationToken));
        Assert.Equal(bytes, destination.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnects_resume_committed_bytes_and_recover_a_lost_completion_ack(bool loseAck)
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.IsLinked));
        var bytes = new byte[Defaults.FileChunkBytes * 8 + 123];
        Random.Shared.NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        using var destination = new DisconnectingStream(async () =>
        {
            pc.Link.Disconnect("upload reconnect test");
            Assert.True(await Wait.UntilAsync(() => !pc.Link.IsLinked));
        }, loseAck);
        var hash = FileHash.Sha256Hex(bytes);
        await using var grant = console.Session.Uploads.Expect(pc.AgentId, destination, bytes.Length, hash);
        Assert.Equal(bytes.Length, await pc.Link.PushFileAsync(grant.Reference, hash, source, TestContext.Current.CancellationToken));
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(bytes.Length, destination.Written);
        Assert.Equal(loseAck ? 1 : 2, destination.Breaks);
    }

    [Fact]
    public async Task Another_PC_and_an_unauthenticated_peer_cannot_use_a_grant()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.InstallEnrolled(console, 1, TimeSpan.FromDays(365));
        await using var other = TestAgent.InstallEnrolled(console, 2, TimeSpan.FromDays(365));
        using var destination = new MemoryStream();
        await using var grant = console.Session.Uploads.Expect(pc.AgentId, destination, 0, FileHash.Sha256Hex([]));
        var request = new FileRequest { Reference = grant.Reference };
        var error = await Assert.ThrowsAsync<RpcException>(() => console.Session.UploadStatusAsync(other.Store.Certificate, request, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
        await Assert.ThrowsAsync<RpcException>(() => console.Session.UploadStatusAsync(null, request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RpcException>(() => console.Session.ReceiveFileAsync(other.Store.Certificate,
            new Reader([new FileChunk { Reference = grant.Reference, Last = true }]), TestContext.Current.CancellationToken));
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("terminal_hash")]
    [InlineData("oversized")]
    public async Task Malformed_or_corrupt_uploads_never_complete(string fault)
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.InstallEnrolled(console, 1, TimeSpan.FromDays(365));
        using var destination = new MemoryStream();
        var hash = FileHash.Sha256Hex(new byte[] { 1 });
        await using var grant = console.Session.Uploads.Expect(pc.AgentId, destination, 1, hash);
        var chunk = new FileChunk { Reference = grant.Reference, Data = ByteString.CopyFrom(new byte[] { 1 }), Last = true, TotalBytes = 1, Sha256 = hash };
        if (fault == "offset") { chunk.Offset = 1; }
        if (fault == "size") { chunk.TotalBytes = 2; }
        if (fault == "hash") { chunk.Data = ByteString.CopyFrom(new byte[] { 2 }); }
        if (fault == "terminal_hash") { chunk.Sha256 = new string('0', 64); }
        if (fault == "oversized") { chunk.Data = ByteString.CopyFrom(new byte[Defaults.FileChunkBytes + 1]); }
        var error = await Assert.ThrowsAsync<RpcException>(() => console.Session.ReceiveFileAsync(pc.Store.Certificate,
            new Reader([chunk]), TestContext.Current.CancellationToken));
        Assert.Equal(fault == "hash" ? StatusCode.DataLoss : StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_partial_local_write_is_truncated_before_resuming()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.InstallEnrolled(console, 1, TimeSpan.FromDays(365));
        using var destination = new PartialWriteStream();
        byte[] bytes = [1, 2, 3, 4];
        var hash = FileHash.Sha256Hex(bytes);
        await using var grant = console.Session.Uploads.Expect(pc.AgentId, destination, bytes.Length, hash);
        var chunk = new FileChunk { Reference = grant.Reference, Data = ByteString.CopyFrom(bytes), Last = true, TotalBytes = bytes.Length, Sha256 = hash };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => console.Session.ReceiveFileAsync(pc.Store.Certificate,
            new Reader([chunk]), TestContext.Current.CancellationToken));
        Assert.Equal(0, destination.Length);
        var status = await console.Session.UploadStatusAsync(pc.Store.Certificate,
            new FileRequest { Reference = grant.Reference }, TestContext.Current.CancellationToken);
        Assert.False(status.Ok);
        Assert.Equal(0, status.BytesStored);
        var ack = await console.Session.ReceiveFileAsync(pc.Store.Certificate, new Reader([chunk]), TestContext.Current.CancellationToken);
        Assert.True(ack.Ok);
        Assert.Equal(bytes, destination.ToArray());
    }

    [Fact]
    public async Task Cancellation_during_reconnect_is_prompt_and_disposal_revokes_the_grant()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.IsLinked));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var destination = new DisconnectingStream(() =>
        {
            pc.Link.Disconnect("cancel upload");
            cancellation.Cancel();
            return Task.CompletedTask;
        }, false);
        using var source = new MemoryStream(new byte[Defaults.FileChunkBytes * 8]);
        var hash = FileHash.Sha256Hex(source.ToArray());
        var grant = console.Session.Uploads.Expect(pc.AgentId, destination, source.Length, hash);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pc.Link.PushFileAsync(grant.Reference, hash, source, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally { await grant.DisposeAsync(); }
        var error = await Assert.ThrowsAsync<RpcException>(() => console.Session.UploadStatusAsync(pc.Store.Certificate,
            new FileRequest { Reference = grant.Reference }, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    private sealed class Reader(IEnumerable<FileChunk> chunks) : IAsyncStreamReader<FileChunk>
    {
        private readonly IEnumerator<FileChunk> _chunks = chunks.GetEnumerator();
        public FileChunk Current => _chunks.Current;
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_chunks.MoveNext());
        }
    }

    private sealed class DisconnectingStream(Func<Task> disconnect, bool loseAck) : MemoryStream
    {
        public long Written { get; private set; }
        public int Breaks { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            Written += buffer.Length;
            if (!loseAck && Breaks < 2) { Breaks++; await disconnect(); }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            if (loseAck && Breaks == 0) { Breaks++; await disconnect(); }
        }
    }

    private sealed class PartialWriteStream : MemoryStream
    {
        private bool _first = true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                await base.WriteAsync(buffer[..1], cancellationToken);
                throw new OperationCanceledException("Simulated cancellation after a partial local write.");
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}

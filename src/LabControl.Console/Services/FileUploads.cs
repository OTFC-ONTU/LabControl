using System.Collections.Concurrent;
using System.Security.Cryptography;
using Grpc.Core;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Services;

/// <summary>Explicit, per-PC upload destinations. The caller owns the stream and must
/// discard it unless the upload completes, and dispose the grant before the stream (D-42).</summary>
public sealed class FileUploads
{
    private readonly ConcurrentDictionary<string, Upload> _uploads = new();

    public Upload Expect(string agentId, Stream destination, long size, string sha256)
    {
        if (string.IsNullOrWhiteSpace(agentId) || size < 0 || !FileHash.LooksLikeSha256(sha256)
            || !destination.CanRead || !destination.CanWrite || !destination.CanSeek || destination.Length != 0)
        {
            throw new ArgumentException("An upload needs a PC, size, SHA-256 and an empty readable, writable, seekable destination.");
        }

        var upload = new Upload(this, agentId, destination, size, sha256);
        _uploads[upload.Reference] = upload;
        return upload;
    }

    private Upload Find(string agentId, string reference)
    {
        if (!_uploads.TryGetValue(reference, out var upload) || upload.AgentId != agentId)
        {
            throw Error(StatusCode.NotFound, "This console is not expecting that upload from this PC.");
        }
        return upload;
    }

    public async Task<FileAck> StatusAsync(string agentId, string reference, CancellationToken token)
    {
        var upload = Find(agentId, reference);
        await upload.Gate.WaitAsync(token);
        try { return upload.Ack(); }
        finally { upload.Gate.Release(); }
    }

    public async Task<FileAck> ReceiveAsync(string agentId, IAsyncStreamReader<FileChunk> incoming, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Defaults.FileChunkTimeout);
        if (!await incoming.MoveNext(timeout.Token))
        {
            throw Error(StatusCode.InvalidArgument, "An upload must contain a terminal chunk, even for an empty file.");
        }
        var upload = Find(agentId, incoming.Current.Reference);
        await upload.Gate.WaitAsync(timeout.Token);
        try
        {
            upload.Ack(); // Refuse a disposed or failed grant before touching its destination.
            if (upload.Complete) { return upload.Ack(); }
            do
            {
                var chunk = incoming.Current;
                if (chunk.Reference != upload.Reference || chunk.Offset != upload.Stored
                    || chunk.Data.Length > Defaults.FileChunkBytes
                    || (chunk.Data.Length == 0 && !chunk.Last)
                    || chunk.Data.Length > upload.Size - upload.Stored)
                {
                    throw Error(StatusCode.InvalidArgument, "Invalid upload reference, offset or chunk size.");
                }
                if (chunk.Last && (chunk.TotalBytes != upload.Size
                    || upload.Stored + chunk.Data.Length != upload.Size
                    || !FileHash.Matches(chunk.Sha256, upload.Sha256)))
                {
                    throw Error(StatusCode.InvalidArgument, "Invalid upload terminal size or hash.");
                }

                // A cancelled local write may have written a prefix. Truncate it before retry.
                try
                {
                    upload.Destination.Position = upload.Stored;
                    await upload.Destination.WriteAsync(chunk.Data.Memory, timeout.Token);
                }
                catch
                {
                    upload.Destination.SetLength(upload.Stored);
                    throw;
                }
                upload.Hash.AppendData(chunk.Data.Span);
                upload.Stored += chunk.Data.Length;
                if (chunk.Data.Length > 0) { timeout.CancelAfter(Defaults.FileChunkTimeout); }
                if (chunk.Last)
                {
                    if (!FileHash.Matches(upload.Sha256, Convert.ToHexStringLower(upload.Hash.GetHashAndReset())))
                    {
                        upload.Failed = true;
                        throw Error(StatusCode.DataLoss, "The uploaded file failed its SHA-256 check; discard the destination.");
                    }
                    // Do not consume the incremental hash and then leave a resumable grant
                    // if flushing is interrupted.
                    upload.Failed = true;
                    await upload.Destination.FlushAsync(timeout.Token);
                    upload.Complete = true;
                    upload.Failed = false;
                    return upload.Ack();
                }
            } while (await incoming.MoveNext(timeout.Token));
            return upload.Ack(); // Premature EOF: the next attempt asks for this offset.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            upload.Failed = true;
            throw Error(StatusCode.Internal, "The console could not store the upload; discard the destination.");
        }
        finally { upload.Gate.Release(); }
    }

    private static RpcException Error(StatusCode code, string message) => new(new Status(code, message));

    public sealed class Upload : IAsyncDisposable
    {
        private readonly FileUploads _owner;
        internal readonly SemaphoreSlim Gate = new(1);
        internal readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal readonly Stream Destination;
        internal readonly string AgentId;
        internal readonly long Size;
        internal readonly string Sha256;
        internal long Stored;
        internal bool Complete;
        internal bool Failed;
        private bool _disposed;

        internal Upload(FileUploads owner, string agentId, Stream destination, long size, string sha256)
        {
            _owner = owner;
            AgentId = agentId;
            Destination = destination;
            Size = size;
            Sha256 = sha256;
        }

        public string Reference { get; } = Guid.NewGuid().ToString("N");

        internal FileAck Ack()
        {
            if (_disposed || Failed) { throw Error(StatusCode.FailedPrecondition, "This upload is closed or failed; request a new upload."); }
            return new FileAck { Reference = Reference, BytesStored = Stored, Ok = Complete, Sha256 = Sha256 };
        }

        public async ValueTask DisposeAsync()
        {
            _owner._uploads.TryRemove(Reference, out _);
            await Gate.WaitAsync();
            try
            {
                if (_disposed) { return; }
                _disposed = true;
                Hash.Dispose();
            }
            finally { Gate.Release(); }
        }
    }
}

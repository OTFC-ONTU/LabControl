using Google.Protobuf;
using Grpc.Core;
using LabControl.Shared.Files;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Link;

public sealed partial class AgentLink
{
    /// <summary>Upload to an explicit console grant (D-42). Keep the source unchanged
    /// and seekable until completion. A reconnect resumes at the console's stored offset.</summary>
    public async Task<long> PushFileAsync(string reference, string sha256, Stream source, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(reference) || !FileHash.LooksLikeSha256(sha256) || !source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("An upload needs a reference, SHA-256 and a readable, seekable source.");
        }
        var size = source.Length;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        inactivity.CancelAfter(Defaults.FileChunkTimeout);
        var buffer = new byte[Defaults.FileChunkBytes];
        long highWater = 0;
        try
        {
            while (true)
            {
                inactivity.Token.ThrowIfCancellationRequested();
                AgentService.AgentServiceClient? client;
                CancellationToken sessionToken;
                lock (_gateLock)
                {
                    client = _client;
                    sessionToken = _session?.Token ?? new CancellationToken(true);
                }
                if (client is null || sessionToken.IsCancellationRequested)
                {
                    await Task.Delay(Defaults.FileRetryDelay, inactivity.Token);
                    continue;
                }
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(inactivity.Token, sessionToken);
                try
                {
                    var status = await client.GetUploadStatusAsync(new FileRequest { Reference = reference }, cancellationToken: attempt.Token);
                    Validate(status);
                    if (status.Ok) { return size; }
                    using var call = client.PushFile(cancellationToken: attempt.Token);
                    var offset = status.BytesStored;
                    source.Position = offset;
                    while (true)
                    {
                        if (source.Length != size) { throw new FilePushException("The upload source changed size."); }
                        var count = await source.ReadAtLeastAsync(buffer, (int)Math.Min(buffer.Length, size - offset),
                            throwOnEndOfStream: false, cancellationToken: attempt.Token);
                        if (count > size - offset || (count == 0 && offset != size))
                        {
                            throw new FilePushException("The upload source changed during transfer.");
                        }
                        var last = offset + count == size;
                        await call.RequestStream.WriteAsync(new FileChunk
                        {
                            Reference = reference, Offset = offset, Data = ByteString.CopyFrom(buffer, 0, count),
                            Last = last, Sha256 = last ? sha256 : "", TotalBytes = last ? size : 0,
                        }, attempt.Token);
                        offset += count;
                        // Re-sending the same prefix must not extend the inactivity budget.
                        if (offset > highWater)
                        {
                            highWater = offset;
                            inactivity.CancelAfter(Defaults.FileChunkTimeout);
                        }
                        if (last) { break; }
                    }
                    await call.RequestStream.CompleteAsync();
                    var ack = await call.ResponseAsync;
                    Validate(ack);
                    if (ack.Ok) { return size; }
                }
                catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.DeadlineExceeded)
                {
                }
                catch (OperationCanceledException) when (sessionToken.IsCancellationRequested && !inactivity.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException) when (sessionToken.IsCancellationRequested)
                {
                }
                catch (RpcException ex)
                {
                    throw new FilePushException($"Uploading failed: {ex.Status.Detail}");
                }
                await Task.Delay(Defaults.FileRetryDelay, inactivity.Token);
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            throw new FilePushException($"The upload made no progress for {Defaults.FileChunkTimeout.TotalSeconds:0} s, including reconnects.");
        }

        void Validate(FileAck ack)
        {
            if (ack.Reference != reference || !FileHash.Matches(sha256, ack.Sha256) || ack.BytesStored < 0 || ack.BytesStored > size || (ack.Ok && ack.BytesStored != size))
            {
                throw new FilePushException("The console returned an invalid upload acknowledgement.");
            }
        }
    }
}

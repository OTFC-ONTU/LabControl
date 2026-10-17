using Grpc.Core;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Shared.Link;

/// <summary>How a <c>PullBroadcast</c> ended, for the PC's log and the tests.</summary>
public enum BroadcastPullEnd
{
    /// <summary>The console ended the stream: the teacher stopped, or the console left the lab.</summary>
    Ended = 0,

    /// <summary>There was no link, or the link it ran on ended.</summary>
    LinkEnded = 1,

    /// <summary>This PC stopped it: a <c>BROADCAST_STOP</c>, a newer broadcast, or its own hard limit.</summary>
    Cancelled = 2,

    /// <summary>The console refused it or the stream failed; see the message.</summary>
    Failed = 3,

    /// <summary>The console is older than M6 portion 2 and has no <c>PullBroadcast</c>.</summary>
    Unsupported = 4,
}

public sealed partial class AgentLink
{
    /// <summary>
    /// Grows by one for every link attempt: a message that arrived on one link and a stream
    /// opened on another can be told apart even when they name the same broadcast (a PC that
    /// relinks is sent <c>BROADCAST_START</c> again before its old stream has wound down).
    /// </summary>
    public long LinkEpoch
    {
        get
        {
            lock (_gateLock)
            {
                return _linkEpoch;
            }
        }
    }

    /// <summary>
    /// Receives the teacher's screen (PROTOCOL <i>Broadcast</i>, M6 portion 2, D-71): dials
    /// <c>PullBroadcast</c> for <paramref name="sessionId"/> on the current link and hands every
    /// frame to <paramref name="onFrame"/>, awaiting it — so a PC that shows frames slowly
    /// holds its own HTTP/2 stream back, and the console's latest-wins slot for this PC does
    /// the dropping. Dies with the link: the call runs on the link session's token and is
    /// never re-dialled here — a PC that links again is told to join again. Never throws
    /// except for <paramref name="token"/>'s own cancellation, which is reported as
    /// <see cref="BroadcastPullEnd.Cancelled"/>.
    /// </summary>
    public async Task<(BroadcastPullEnd End, string Detail)> PullBroadcastAsync(string sessionId, Func<VideoFrame, CancellationToken, ValueTask> onFrame, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(onFrame);

        AgentService.AgentServiceClient? client;
        CancellationToken sessionToken;
        lock (_gateLock)
        {
            client = _client;
            sessionToken = _session?.Token ?? new CancellationToken(canceled: true);
        }

        if (client is null || sessionToken.IsCancellationRequested)
        {
            return (BroadcastPullEnd.LinkEnded, "this PC is not linked to a console right now");
        }

        using var call = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken, _stopping.Token);
        long frames = 0;
        try
        {
            using var stream = client.PullBroadcast(new BroadcastRequest { SessionId = sessionId }, cancellationToken: call.Token);
            while (await stream.ResponseStream.MoveNext(call.Token))
            {
                var frame = stream.ResponseStream.Current;
                if (frame.Jpeg.Length > Defaults.VideoFrameMaxBytes)
                {
                    _log.LogWarning("{Pc}: dropped a {Kib} KiB broadcast frame, above the limit", Name, frame.Jpeg.Length / 1024);
                    continue;
                }

                frames++;
                try
                {
                    await onFrame(frame, call.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Showing one frame is the machine's problem; the stream goes on.
                    _log.LogDebug(ex, "{Pc}: showing broadcast frame {Seq} failed: {Message}", Name, frame.Seq, ex.Message);
                }
            }

            _log.LogInformation("{Pc}: broadcast {Session} ended by the console after {Frames} frames", Name, sessionId, frames);
            return (BroadcastPullEnd.Ended, string.Empty);
        }
        catch (Exception ex) when (ex is OperationCanceledException || (ex is RpcException { StatusCode: StatusCode.Cancelled } && call.IsCancellationRequested))
        {
            return token.IsCancellationRequested || _stopping.IsCancellationRequested
                ? (BroadcastPullEnd.Cancelled, string.Empty)
                : (BroadcastPullEnd.LinkEnded, "the link ended");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            _log.LogWarning("{Pc}: the console has no PullBroadcast; update the console", Name);
            return (BroadcastPullEnd.Unsupported, "the console does not offer a broadcast; update the console");
        }
        catch (RpcException ex)
        {
            _log.LogWarning("{Pc}: broadcast {Session} stream failed: {Status} {Detail}", Name, sessionId, ex.StatusCode, ex.Status.Detail);
            return sessionToken.IsCancellationRequested
                ? (BroadcastPullEnd.LinkEnded, "the link ended")
                : (BroadcastPullEnd.Failed, ex.Status.Detail.Length > 0 ? ex.Status.Detail : ex.StatusCode.ToString());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or ObjectDisposedException)
        {
            _log.LogWarning("{Pc}: broadcast {Session} stream failed: {Message}", Name, sessionId, ex.Message);
            return sessionToken.IsCancellationRequested ? (BroadcastPullEnd.LinkEnded, "the link ended") : (BroadcastPullEnd.Failed, ex.Message);
        }
    }
}

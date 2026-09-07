using System.Threading.Channels;
using Grpc.Core;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Shared.Video;

/// <summary>
/// The agent's end of <c>PushVideo</c> (PROTOCOL "Video", D-34): a producer offers frames,
/// the uplink sends them on one client-streaming call that it opens on the first frame and
/// closes when video is switched off or the link ends. The queue holds one frame beyond
/// the one on the wire; a producer whose frame is refused keeps its dirty state and tries
/// again — video is latest-wins, and a slow console is never allowed to pile up frames on
/// the PC. One uplink lives exactly as long as one link session.
/// </summary>
public sealed class VideoUplink : IAsyncDisposable
{
    private readonly AgentService.AgentServiceClient _client;
    private readonly string _agentId;
    private readonly ILogger _log;
    private readonly CancellationToken _linkToken;
    private readonly Action<string, string>? _report;
    private readonly Lock _lock = new();

    private Channel<VideoFrame>? _queue;
    private Task? _sender;
    private int _generation;
    private ulong _seq;
    private bool _unsupportedReported;

    public VideoUplink(AgentService.AgentServiceClient client, string agentId, ILogger log, CancellationToken linkToken, Action<string, string>? report = null)
    {
        _client = client;
        _agentId = agentId;
        _log = log;
        _linkToken = linkToken;
        _report = report;
    }

    /// <summary>Frames the console accepted on this uplink so far, for a simulator's display and the tests.</summary>
    public long FramesSent { get; private set; }

    public long BytesSent { get; private set; }

    /// <summary>Frames refused because the previous one was still in flight.</summary>
    public long FramesDropped { get; private set; }

    /// <summary>
    /// Offers a frame. Fills in <c>agent_id</c>, <c>seq</c> and the timestamp. Returns
    /// <c>false</c> when the frame was not queued — the wire is busy, or the uplink is closed —
    /// and the producer should keep what the frame carried for the next attempt.
    /// </summary>
    public bool TryOffer(VideoFrame frame, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_lock)
        {
            if (_linkToken.IsCancellationRequested)
            {
                return false;
            }

            if (_queue is null)
            {
                var queue = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(Defaults.VideoUplinkQueueLength)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropWrite,
                });
                var generation = ++_generation;
                _queue = queue;
                _sender = Task.Run(() => SendAsync(queue.Reader, generation));
            }

            frame.AgentId = _agentId;
            frame.Seq = ++_seq;
            frame.AtUnixMs = now.ToUnixTimeMilliseconds();

            if (_queue.Writer.TryWrite(frame))
            {
                return true;
            }

            FramesDropped++;
            return false;
        }
    }

    /// <summary>
    /// Ends the current call (video switched off). Frames already queued are still sent;
    /// the next <see cref="TryOffer"/> opens a new call.
    /// </summary>
    public async Task StopAsync()
    {
        Task? sender;
        lock (_lock)
        {
            _queue?.Writer.TryComplete();
            _queue = null;
            sender = _sender;
            _sender = null;
        }

        if (sender is not null)
        {
            await SwallowAsync(sender);
        }
    }

    private async Task SendAsync(ChannelReader<VideoFrame> frames, int generation)
    {
        AsyncClientStreamingCall<VideoFrame, VideoAck>? call = null;
        try
        {
            await foreach (var frame in frames.ReadAllAsync(_linkToken))
            {
                call ??= _client.PushVideo(cancellationToken: _linkToken);
                await call.RequestStream.WriteAsync(frame, _linkToken);
                FramesSent++;
                BytesSent += frame.Jpeg.Length;
            }

            if (call is not null)
            {
                await call.RequestStream.CompleteAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_linkToken);
                timeout.CancelAfter(Defaults.VideoCloseTimeout);
                await call.ResponseAsync.WaitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            // An older console: no screens, but everything else keeps working (PROTOCOL "Versioning").
            if (!_unsupportedReported)
            {
                _unsupportedReported = true;
                _log.LogWarning("the console does not accept video (PushVideo unimplemented); screens are off until it is updated");
                _report?.Invoke("video.unsupported", "This console does not accept screen video; update the console.");
            }
        }
        catch (RpcException ex) when (!_linkToken.IsCancellationRequested)
        {
            _log.LogWarning("the video stream ended: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _log.LogWarning("the video stream ended: {Message}", ex.Message);
        }
        finally
        {
            call?.Dispose();
            lock (_lock)
            {
                // Whatever ended the call, the next frame starts a fresh one — unless a
                // StopAsync already replaced this queue, in which case the newer one stays.
                if (_generation == generation)
                {
                    _queue = null;
                    _sender = null;
                }
            }
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

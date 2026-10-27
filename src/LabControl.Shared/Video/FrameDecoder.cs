using LabControl.Shared.Protocol;
using LabControl.Shared.Video.H264;

namespace LabControl.Shared.Video;

/// <summary>
/// One stream's way into a <see cref="ScreenImage"/> (D-91 item 2): a frame is decoded by the
/// codec it names — a JPEG straight into the picture, an H.264 access unit through this
/// stream's own decoder, made on the first IDR. A non-IDR unit with no decoder, or one the
/// decoder refuses, is <see cref="FrameOutcome.NeedsKeyframe"/>: the decoder is dropped and
/// the next IDR starts it again. Thread-safe; a stream applies its frames in order from one
/// thread at a time.
/// </summary>
public sealed class FrameDecoder : IDisposable
{
    private readonly Func<IH264Decoder> _create;
    private readonly Lock _lock = new();
    private IH264Decoder? _h264;
    private bool _disposed;

    public FrameDecoder(Func<IH264Decoder>? create = null) => _create = create ?? H264Codecs.CreateDecoder;

    /// <summary>The codec of the last frame applied: <see cref="Defaults.VideoCodecJpeg"/> or <see cref="Defaults.VideoCodecH264"/>; <c>null</c> before one.</summary>
    public string? LastCodec { get; private set; }

    /// <summary>Why the last H.264 frame could not be decoded, for a log line; <c>null</c> after one that could.</summary>
    public string? Problem { get; private set; }

    /// <summary>A codec name as frames carry it: empty (an older build) is JPEG.</summary>
    public static string CodecOf(VideoFrame frame) =>
        string.Equals(frame.Codec, Defaults.VideoCodecH264, StringComparison.OrdinalIgnoreCase) ? Defaults.VideoCodecH264 : Defaults.VideoCodecJpeg;

    public FrameOutcome Apply(ScreenImage image, VideoFrame frame, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(frame);
        lock (_lock)
        {
            if (_disposed)
            {
                return FrameOutcome.Undecodable;
            }

            var codec = CodecOf(frame);
            if (codec == Defaults.VideoCodecJpeg)
            {
                // Back to Text: the H.264 decoder is not needed any more.
                DropDecoder();
                var outcome = image.Apply(frame, now);
                if (outcome == FrameOutcome.Applied)
                {
                    LastCodec = codec;
                }

                return outcome;
            }

            if (_h264 is null)
            {
                if (!frame.Keyframe)
                {
                    return FrameOutcome.NeedsKeyframe;
                }

                try
                {
                    _h264 = _create();
                }
                catch (Exception ex)
                {
                    Problem = ex.Message;
                    return FrameOutcome.Undecodable;
                }
            }

            try
            {
                var picture = _h264.Decode(frame.Jpeg.Span);
                if (picture is null)
                {
                    // Taken by the decoder, nothing to show yet; the picture stays as it was.
                    return FrameOutcome.Applied;
                }

                if (frame.Width > 0 && frame.Height > 0 && (picture.Width > frame.Width || picture.Height > frame.Height))
                {
                    DropDecoder();
                    return FrameOutcome.Malformed;
                }

                image.Replace(picture, frame.Width > 0 ? frame.Width : picture.Width, frame.Height > 0 ? frame.Height : picture.Height, now);
                LastCodec = codec;
                Problem = null;
                return FrameOutcome.Applied;
            }
            catch (Exception ex)
            {
                Problem = ex.Message;
                DropDecoder();
                return FrameOutcome.NeedsKeyframe;
            }
        }
    }

    /// <summary>Forgets the stream: the next H.264 frame must be an IDR.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            DropDecoder();
            LastCodec = null;
        }
    }

    private void DropDecoder()
    {
        try
        {
            _h264?.Dispose();
        }
        catch (Exception)
        {
        }

        _h264 = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            DropDecoder();
        }
    }
}

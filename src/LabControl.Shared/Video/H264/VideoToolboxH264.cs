using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using static LabControl.Shared.Video.H264.VideoToolbox;

namespace LabControl.Shared.Video.H264;

/// <summary>
/// H.264 encoding on the macOS console (D-91 item 1): a real-time, baseline-profile
/// <c>VTCompressionSession</c> without frame reordering, BGRA in, each picture flushed with
/// <c>VTCompressionSessionCompleteFrames</c> so it is one call in, one access unit out.
/// VideoToolbox picks Apple's hardware encoder by itself.
/// </summary>
public sealed unsafe class VideoToolboxH264Encoder : IH264Encoder
{
    private readonly GCHandle _self;
    private IntPtr _session;
    private long _frame;
    private readonly int _frameRate;

    // Written by the output callback, read after CompleteFrames returns.
    private byte[]? _output;
    private bool _outputKeyframe;
    private string? _outputProblem;

    public VideoToolboxH264Encoder(int width, int height, double framesPerSecond, int bitsPerSecond)
    {
        if (!Available)
        {
            throw new H264Exception("VideoToolbox is only on macOS");
        }

        Width = width;
        Height = height;
        _frameRate = Math.Max(1, (int)Math.Round(framesPerSecond));
        _self = GCHandle.Alloc(this);
        var status = VTCompressionSessionCreate(IntPtr.Zero, width, height, CodecH264, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            &OnEncoded, GCHandle.ToIntPtr(_self), out _session);
        if (status != 0 || _session == IntPtr.Zero)
        {
            _self.Free();
            throw new H264Exception($"VTCompressionSessionCreate failed ({status})");
        }

        try
        {
            Set("kVTCompressionPropertyKey_RealTime", BooleanTrue, owned: false);
            Set("kVTCompressionPropertyKey_ProfileLevel", VT("kVTProfileLevel_H264_Baseline_AutoLevel"), owned: false);
            Set("kVTCompressionPropertyKey_AllowFrameReordering", BooleanFalse, owned: false);
            Set("kVTCompressionPropertyKey_AverageBitRate", Int32(bitsPerSecond), owned: true);
            // The producer asks for every IDR it wants (KeyframeInterval and the PCs' requests).
            Set("kVTCompressionPropertyKey_MaxKeyFrameInterval", Int32(int.MaxValue), owned: true, optional: true);
            Set("kVTCompressionPropertyKey_ExpectedFrameRate", Int32(_frameRate), owned: true, optional: true);
            // Bytes in one second, so a burst cannot run far past the cap the pacer enforces.
            Set("kVTCompressionPropertyKey_DataRateLimits", VideoToolbox.Array(Int32(bitsPerSecond / 8), Float64(1)), owned: true, optional: true);
            VTCompressionSessionPrepareToEncodeFrames(_session);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Kind => "videotoolbox";

    public int Width { get; }

    public int Height { get; }

    private void Set(string key, IntPtr value, bool owned, bool optional = false)
    {
        var status = VTSessionSetProperty(_session, VT(key), value);
        if (owned)
        {
            Release(value);
        }

        if (status != 0 && !optional)
        {
            throw new H264Exception($"VideoToolbox refused {key} ({status})");
        }
    }

    public byte[]? Encode(ReadOnlySpan<byte> bgra, int rowBytes, bool keyframe, out bool isKeyframe)
    {
        isKeyframe = false;
        if (_session == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(VideoToolboxH264Encoder));
        }

        if (rowBytes < Width * 4 || bgra.Length < (long)rowBytes * (Height - 1) + Width * 4)
        {
            throw new ArgumentException("The picture is smaller than the encoder.", nameof(bgra));
        }

        var pixels = CreatePixelBuffer();
        var properties = IntPtr.Zero;
        try
        {
            CVPixelBufferLockBaseAddress(pixels, 0);
            try
            {
                var target = (byte*)CVPixelBufferGetBaseAddress(pixels);
                var stride = (int)CVPixelBufferGetBytesPerRow(pixels);
                fixed (byte* source = bgra)
                {
                    for (var y = 0; y < Height; y++)
                    {
                        Buffer.MemoryCopy(source + (long)y * rowBytes, target + (long)y * stride, stride, Width * 4);
                    }
                }
            }
            finally
            {
                CVPixelBufferUnlockBaseAddress(pixels, 0);
            }

            if (keyframe)
            {
                properties = Dictionary((VT("kVTEncodeFrameOptionKey_ForceKeyFrame"), BooleanTrue, false));
            }

            _output = null;
            _outputProblem = null;
            var status = VTCompressionSessionEncodeFrame(_session, pixels, CMTime.Make(_frame++, _frameRate), CMTime.Invalid, properties, IntPtr.Zero, out _);
            if (status != 0)
            {
                throw new H264Exception($"VTCompressionSessionEncodeFrame failed ({status})");
            }

            status = VTCompressionSessionCompleteFrames(_session, CMTime.Invalid);
            if (status != 0)
            {
                throw new H264Exception($"VTCompressionSessionCompleteFrames failed ({status})");
            }
        }
        finally
        {
            Release(properties);
            Release(pixels);
        }

        if (_outputProblem is not null)
        {
            throw new H264Exception(_outputProblem);
        }

        isKeyframe = _outputKeyframe;
        return _output;
    }

    private IntPtr CreatePixelBuffer()
    {
        IntPtr pixels;
        var pool = VTCompressionSessionGetPixelBufferPool(_session);
        if (pool != IntPtr.Zero && CVPixelBufferPoolCreatePixelBuffer(IntPtr.Zero, pool, out pixels) == 0 && pixels != IntPtr.Zero
            && CVPixelBufferGetPixelFormatType(pixels) == PixelFormatBgra)
        {
            return pixels;
        }

        var status = CVPixelBufferCreate(IntPtr.Zero, (nuint)Width, (nuint)Height, PixelFormatBgra, IntPtr.Zero, out pixels);
        if (status != 0 || pixels == IntPtr.Zero)
        {
            throw new H264Exception($"CVPixelBufferCreate failed ({status})");
        }

        return pixels;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEncoded(IntPtr refCon, IntPtr sourceFrameRefCon, int status, uint infoFlags, IntPtr sampleBuffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(refCon).Target is not VideoToolboxH264Encoder encoder)
            {
                return;
            }

            if (status != 0)
            {
                encoder._outputProblem = $"the encoder reported {status}";
                return;
            }

            if (sampleBuffer == IntPtr.Zero)
            {
                return; // dropped by the encoder
            }

            encoder.TakeOutput(sampleBuffer);
        }
        catch (Exception ex)
        {
            // Never let an exception cross into VideoToolbox's thread.
            if (GCHandle.FromIntPtr(refCon).Target is VideoToolboxH264Encoder encoder)
            {
                encoder._outputProblem = ex.Message;
            }
        }
    }

    private void TakeOutput(IntPtr sampleBuffer)
    {
        var block = CMSampleBufferGetDataBuffer(sampleBuffer);
        if (block == IntPtr.Zero)
        {
            return;
        }

        var length = (int)CMBlockBufferGetDataLength(block);
        var units = new byte[length];
        fixed (byte* target = units)
        {
            if (CMBlockBufferCopyDataBytes(block, 0, (nuint)length, target) != 0)
            {
                _outputProblem = "the encoded picture could not be read";
                return;
            }
        }

        var annexB = new List<byte>(length + 64);
        var format = CMSampleBufferGetFormatDescription(sampleBuffer);
        var body = new List<byte>(length + 16);
        if (!AnnexB.AppendLengthPrefixed(units, body))
        {
            _outputProblem = "the encoder's units were malformed";
            return;
        }

        var keyframe = AnnexB.IsIdr(body.ToArray());
        if (keyframe && format != IntPtr.Zero)
        {
            // An IDR carries its SPS and PPS in front, so a PC that joins on it needs nothing else.
            if (CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, 0, out _, out _, out var count, out _) == 0)
            {
                for (nuint i = 0; i < count; i++)
                {
                    if (CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, i, out var pointer, out var size, out _, out _) == 0)
                    {
                        AnnexB.Append(new ReadOnlySpan<byte>((void*)pointer, (int)size), annexB);
                    }
                }
            }
        }

        annexB.AddRange(body);
        _output = annexB.ToArray();
        _outputKeyframe = keyframe;
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero)
        {
            VTCompressionSessionInvalidate(_session);
            Release(_session);
            _session = IntPtr.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}

/// <summary>
/// H.264 decoding on the macOS console (D-91 item 1): a <c>VTDecompressionSession</c> made
/// from the SPS and PPS in front of an IDR — made again when they change — with BGRA output
/// copied into one reused bitmap. Decoding is synchronous: the callback has run when
/// <see cref="Decode"/> returns.
/// </summary>
public sealed unsafe class VideoToolboxH264Decoder : IH264Decoder
{
    private readonly GCHandle _self;
    private IntPtr _format;
    private IntPtr _session;
    private byte[]? _sps;
    private byte[]? _pps;
    private SKBitmap? _picture;
    private bool _pictureReady;
    private string? _problem;

    public VideoToolboxH264Decoder()
    {
        if (!Available)
        {
            throw new H264Exception("VideoToolbox is only on macOS");
        }

        _self = GCHandle.Alloc(this);
    }

    public string Kind => "videotoolbox";

    public SKBitmap? Decode(ReadOnlySpan<byte> accessUnit)
    {
        if (AnnexB.ParameterSets(accessUnit) is { } sets && (_sps is null || !_sps.AsSpan().SequenceEqual(sets.Sps) || !_pps!.AsSpan().SequenceEqual(sets.Pps)))
        {
            Open(sets.Sps, sets.Pps);
        }

        if (_session == IntPtr.Zero)
        {
            throw new H264Exception("no SPS and PPS yet: the stream must start at an IDR");
        }

        var data = AnnexB.ToLengthPrefixed(accessUnit);
        if (data.Length == 0)
        {
            return null;
        }

        IntPtr block = IntPtr.Zero;
        IntPtr sample = IntPtr.Zero;
        try
        {
            var status = CMBlockBufferCreateWithMemoryBlock(IntPtr.Zero, IntPtr.Zero, (nuint)data.Length, IntPtr.Zero, IntPtr.Zero, 0, (nuint)data.Length,
                BlockBufferAssureMemoryNow, out block);
            if (status != 0)
            {
                throw new H264Exception($"CMBlockBufferCreateWithMemoryBlock failed ({status})");
            }

            fixed (byte* source = data)
            {
                status = CMBlockBufferReplaceDataBytes(source, block, 0, (nuint)data.Length);
            }

            if (status != 0)
            {
                throw new H264Exception($"CMBlockBufferReplaceDataBytes failed ({status})");
            }

            var size = (nuint)data.Length;
            status = CMSampleBufferCreateReady(IntPtr.Zero, block, _format, 1, 0, IntPtr.Zero, 1, &size, out sample);
            if (status != 0)
            {
                throw new H264Exception($"CMSampleBufferCreateReady failed ({status})");
            }

            _pictureReady = false;
            _problem = null;
            status = VTDecompressionSessionDecodeFrame(_session, sample, 0, IntPtr.Zero, out _);
            if (status != 0)
            {
                throw new H264Exception($"VTDecompressionSessionDecodeFrame failed ({status})");
            }

            VTDecompressionSessionWaitForAsynchronousFrames(_session);
        }
        finally
        {
            Release(sample);
            Release(block);
        }

        if (_problem is not null)
        {
            throw new H264Exception(_problem);
        }

        return _pictureReady ? _picture : null;
    }

    private void Open(byte[] sps, byte[] pps)
    {
        Close();
        var pointers = stackalloc IntPtr[2];
        var sizes = stackalloc nuint[2];
        IntPtr format;
        int status;
        fixed (byte* s = sps)
        fixed (byte* p = pps)
        {
            pointers[0] = (IntPtr)s;
            pointers[1] = (IntPtr)p;
            sizes[0] = (nuint)sps.Length;
            sizes[1] = (nuint)pps.Length;
            status = CMVideoFormatDescriptionCreateFromH264ParameterSets(IntPtr.Zero, 2, pointers, sizes, 4, out format);
        }

        if (status != 0 || format == IntPtr.Zero)
        {
            throw new H264Exception($"the stream's SPS/PPS were refused ({status})");
        }

        var attributes = Dictionary((CV("kCVPixelBufferPixelFormatTypeKey"), Int32(unchecked((int)PixelFormatBgra)), true));
        var callback = new DecompressionOutputCallbackRecord
        {
            Callback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, uint, IntPtr, CMTime, CMTime, void>)&OnDecoded,
            RefCon = GCHandle.ToIntPtr(_self),
        };
        status = VTDecompressionSessionCreate(IntPtr.Zero, format, IntPtr.Zero, attributes, &callback, out var session);
        Release(attributes);
        if (status != 0 || session == IntPtr.Zero)
        {
            Release(format);
            throw new H264Exception($"VTDecompressionSessionCreate failed ({status})");
        }

        _format = format;
        _session = session;
        _sps = sps;
        _pps = pps;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDecoded(IntPtr refCon, IntPtr sourceFrameRefCon, int status, uint infoFlags, IntPtr imageBuffer, CMTime presentation, CMTime duration)
    {
        try
        {
            if (GCHandle.FromIntPtr(refCon).Target is not VideoToolboxH264Decoder decoder)
            {
                return;
            }

            if (status != 0)
            {
                decoder._problem = $"the decoder reported {status}";
                return;
            }

            if (imageBuffer != IntPtr.Zero)
            {
                decoder.TakePicture(imageBuffer);
            }
        }
        catch (Exception ex)
        {
            if (GCHandle.FromIntPtr(refCon).Target is VideoToolboxH264Decoder decoder)
            {
                decoder._problem = ex.Message;
            }
        }
    }

    private void TakePicture(IntPtr pixels)
    {
        var width = (int)CVPixelBufferGetWidth(pixels);
        var height = (int)CVPixelBufferGetHeight(pixels);
        if (CVPixelBufferGetPixelFormatType(pixels) != PixelFormatBgra || width <= 0 || height <= 0)
        {
            _problem = "the decoder did not give BGRA";
            return;
        }

        if (_picture is null || _picture.Width != width || _picture.Height != height)
        {
            _picture?.Dispose();
            _picture = new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
        }

        CVPixelBufferLockBaseAddress(pixels, LockReadOnly);
        try
        {
            var source = (byte*)CVPixelBufferGetBaseAddress(pixels);
            var stride = (int)CVPixelBufferGetBytesPerRow(pixels);
            var target = (byte*)_picture.GetPixels();
            var targetStride = _picture.RowBytes;
            for (var y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(source + (long)y * stride, target + (long)y * targetStride, targetStride, width * 4);
            }
        }
        finally
        {
            CVPixelBufferUnlockBaseAddress(pixels, LockReadOnly);
        }

        _pictureReady = true;
    }

    private void Close()
    {
        if (_session != IntPtr.Zero)
        {
            VTDecompressionSessionInvalidate(_session);
            Release(_session);
            _session = IntPtr.Zero;
        }

        Release(_format);
        _format = IntPtr.Zero;
        _sps = null;
        _pps = null;
    }

    public void Dispose()
    {
        Close();
        _picture?.Dispose();
        _picture = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}

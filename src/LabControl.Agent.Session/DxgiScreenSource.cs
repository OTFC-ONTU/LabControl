using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using System.Runtime.CompilerServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ResultCode = Vortice.DXGI.ResultCode;

namespace LabControl.Agent.Session;

/// <summary>
/// The screen through DXGI Desktop Duplication (D-35): the primary output duplicated on a
/// D3D11 device, every new desktop image copied into a staging texture the CPU can read,
/// the dirty and move rectangles Windows reports handed on as-is. The staging texture is
/// the persistent picture, so a keyframe costs no capture. When the desktop switches (lock
/// screen, UAC) the duplication is lost; the source follows the input desktop and
/// duplicates again, and only gives up — with a reason — when Windows will not let it.
/// </summary>
internal sealed class DxgiScreenSource : IScreenSource
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _width;
    private int _height;
    private bool _hasPicture;
    private string _desktop = string.Empty;
    private RawRect[] _dirtyBuffer = new RawRect[64];
    private OutduplMoveRect[] _moveBuffer = new OutduplMoveRect[16];

    public string Kind => "dxgi";

    /// <summary>Opens the duplication or throws <see cref="ScreenCaptureException"/> with the reason.</summary>
    public static DxgiScreenSource Open()
    {
        var source = new DxgiScreenSource();
        try
        {
            source.Duplicate();
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public IScreenFrame? Acquire(TimeSpan timeout)
    {
        if (_duplication is null)
        {
            Duplicate();
        }

        var result = _duplication!.AcquireNextFrame((uint)Math.Max(0, timeout.TotalMilliseconds), out var info, out var resource);
        if (result.Code == ResultCode.WaitTimeout.Code)
        {
            return _hasPicture ? new Frame(this, []) : null;
        }

        if (result.Code == ResultCode.AccessLost.Code || result.Code == ResultCode.InvalidCall.Code)
        {
            // The desktop switched or the display mode changed: follow it and duplicate again.
            Release();
            Duplicate();
            return _hasPicture ? new Frame(this, []) : null;
        }

        if (result.Failure)
        {
            throw Failure("acquire_failed", "AcquireNextFrame", result);
        }

        try
        {
            var dirty = new List<Rect>();
            if (info.LastPresentTime != 0)
            {
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                _context!.CopyResource(_staging!, texture);
                if (!_hasPicture)
                {
                    _hasPicture = true;
                    dirty.Add(VideoGeometry.Whole(_width, _height));
                }
                else if (info.TotalMetadataBufferSize > 0)
                {
                    CollectDirty(dirty);
                }
            }

            return _hasPicture ? new Frame(this, dirty) : null;
        }
        finally
        {
            resource?.Dispose();
            try
            {
                _duplication.ReleaseFrame();
            }
            catch (SharpGenException)
            {
                // Already lost; the next Acquire duplicates again.
            }
        }
    }

    private void CollectDirty(List<Rect> dirty)
    {
        var duplication = _duplication!;
        var moveSize = (uint)Unsafe.SizeOf<OutduplMoveRect>();
        var rectSize = (uint)Unsafe.SizeOf<RawRect>();

        uint moveBytes;
        while (true)
        {
            var result = duplication.GetFrameMoveRects((uint)_moveBuffer.Length * moveSize, _moveBuffer, out moveBytes);
            if (result.Code == ResultCode.MoreData.Code)
            {
                _moveBuffer = new OutduplMoveRect[Math.Max(_moveBuffer.Length * 2, (int)(moveBytes / moveSize) + 1)];
                continue;
            }

            if (result.Failure)
            {
                throw Failure("metadata_failed", "GetFrameMoveRects", result);
            }

            break;
        }

        for (var i = 0; i < moveBytes / moveSize; i++)
        {
            var move = _moveBuffer[i];
            var destination = ToRect(move.DestinationRect);
            dirty.Add(destination);
            dirty.Add(new Rect { X = move.SourcePoint.X, Y = move.SourcePoint.Y, Width = destination.Width, Height = destination.Height });
        }

        uint dirtyBytes;
        while (true)
        {
            var result = duplication.GetFrameDirtyRects((uint)_dirtyBuffer.Length * rectSize, _dirtyBuffer, out dirtyBytes);
            if (result.Code == ResultCode.MoreData.Code)
            {
                _dirtyBuffer = new RawRect[Math.Max(_dirtyBuffer.Length * 2, (int)(dirtyBytes / rectSize) + 1)];
                continue;
            }

            if (result.Failure)
            {
                throw Failure("metadata_failed", "GetFrameDirtyRects", result);
            }

            break;
        }

        for (var i = 0; i < dirtyBytes / rectSize; i++)
        {
            dirty.Add(ToRect(_dirtyBuffer[i]));
        }
    }

    private static Rect ToRect(RawRect rect) => new()
    {
        X = rect.Left,
        Y = rect.Top,
        Width = rect.Right - rect.Left,
        Height = rect.Bottom - rect.Top,
    };

    // ------------------------------------------------------------------ setup

    private void Duplicate()
    {
        try
        {
            _desktop = DesktopAccess.AttachToInputDesktop();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            throw new ScreenCaptureException("no_desktop", ex.Message, ex);
        }

        if (_device is null)
        {
            var created = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out _device, out _context);
            if (created.Failure || _device is null || _context is null)
            {
                throw Failure("no_device", "D3D11CreateDevice", created);
            }
        }

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var output = PrimaryOutput(adapter);
        using var output1 = output.QueryInterface<IDXGIOutput1>();

        IDXGIOutputDuplication duplication;
        try
        {
            duplication = output1.DuplicateOutput(_device);
        }
        catch (SharpGenException ex)
        {
            var duplicated = ex.ResultCode;
            var reason = duplicated.Code == ResultCode.NotCurrentlyAvailable.Code ? "duplication_busy"
                : duplicated.Code == ResultCode.Unsupported.Code ? "no_duplication"
                : duplicated.Code == Result.AccessDenied.Code ? "duplication_denied"
                : "duplication_failed";
            throw Failure(reason, "DuplicateOutput", duplicated);
        }

        _duplication = duplication;
        var description = duplication.Description;
        var width = (int)description.ModeDescription.Width;
        var height = (int)description.ModeDescription.Height;

        if (_staging is null || width != _width || height != _height)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
            });
            _width = width;
            _height = height;
            _hasPicture = false;
        }
    }

    private static IDXGIOutput PrimaryOutput(IDXGIAdapter adapter)
    {
        IDXGIOutput? first = null;
        for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
        {
            var description = output.Description;
            if (description.AttachedToDesktop && description.DesktopCoordinates.Left == 0 && description.DesktopCoordinates.Top == 0)
            {
                first?.Dispose();
                return output;
            }

            if (first is null)
            {
                first = output;
            }
            else
            {
                output.Dispose();
            }
        }

        return first ?? throw new ScreenCaptureException("no_output", "the graphics adapter has no display output attached to the desktop");
    }

    private void Release()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    private static ScreenCaptureException Failure(string reason, string call, Result result) =>
        new(reason, $"{call} failed: {result.Description} (0x{result.Code:X8}); desktop duplication is not available");

    public void Dispose()
    {
        Release();
        _staging?.Dispose();
        _staging = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    /// <summary>The staging texture mapped for reading; unmapped on dispose.</summary>
    private sealed unsafe class Frame : IScreenFrame
    {
        private readonly DxgiScreenSource _source;
        private readonly MappedSubresource _mapped;

        public Frame(DxgiScreenSource source, IReadOnlyList<Rect> dirty)
        {
            _source = source;
            Dirty = dirty;
            var mapped = source._context!.Map(source._staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out _mapped);
            if (mapped.Failure)
            {
                throw Failure("map_failed", "Map", mapped);
            }
        }

        public int Width => _source._width;

        public int Height => _source._height;

        public int RowBytes => (int)_mapped.RowPitch;

        public ReadOnlySpan<byte> Pixels => new((void*)_mapped.DataPointer, checked((int)(_mapped.RowPitch * (uint)_source._height)));

        public IReadOnlyList<Rect> Dirty { get; }

        public void Dispose() => _source._context?.Unmap(_source._staging!, 0);
    }
}

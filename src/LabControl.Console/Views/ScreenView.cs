using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LabControl.Shared.Video;

namespace LabControl.Console.Views;

/// <summary>
/// Draws a <see cref="ScreenImage"/> — a PC's thumbnail or its full picture — fitted into
/// the control (M3). The pixels are copied into a bitmap only when the image's version
/// moved past what was drawn last; <see cref="Frame"/> is bound to a counter the view model
/// bumps on the UI thread, which is what makes the control redraw.
/// </summary>
public sealed class ScreenView : Control
{
    public static readonly StyledProperty<ScreenImage?> ImageProperty =
        AvaloniaProperty.Register<ScreenView, ScreenImage?>(nameof(Image));

    public static readonly StyledProperty<long> FrameProperty =
        AvaloniaProperty.Register<ScreenView, long>(nameof(Frame));

    /// <summary>Dim the picture: the PC is offline or its stream stalled, so this is history.</summary>
    public static readonly StyledProperty<bool> IsStaleProperty =
        AvaloniaProperty.Register<ScreenView, bool>(nameof(IsStale));

    private WriteableBitmap? _bitmap;
    private long _copiedVersion = -1;

    static ScreenView()
    {
        AffectsRender<ScreenView>(ImageProperty, FrameProperty, IsStaleProperty);
    }

    public ScreenView()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public ScreenImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public long Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public bool IsStale
    {
        get => GetValue(IsStaleProperty);
        set => SetValue(IsStaleProperty, value);
    }

    /// <summary>Where the picture was last drawn inside the control, for mapping pointer positions (portion 3).</summary>
    public Rect PictureBounds { get; private set; }

    public override void Render(DrawingContext context)
    {
        var image = Image;
        if (image is null || !image.HasFrame || image.Width <= 0 || image.Height <= 0)
        {
            PictureBounds = default;
            return;
        }

        var width = image.Width;
        var height = image.Height;
        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _copiedVersion = -1;
        }

        var version = image.Version;
        if (version != _copiedVersion)
        {
            using (var framebuffer = _bitmap.Lock())
            {
                if (image.CopyTo(framebuffer.Address, framebuffer.Size.Width, framebuffer.Size.Height, framebuffer.RowBytes))
                {
                    _copiedVersion = version;
                }
            }
        }

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(bounds.Width / width, bounds.Height / height);
        var drawn = new Size(width * scale, height * scale);
        var destination = new Rect((bounds.Width - drawn.Width) / 2, (bounds.Height - drawn.Height) / 2, drawn.Width, drawn.Height);
        PictureBounds = destination;

        using (context.PushOpacity(IsStale ? 0.45 : 1))
        {
            context.DrawImage(_bitmap, new Rect(0, 0, width, height), destination);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _bitmap?.Dispose();
        _bitmap = null;
        _copiedVersion = -1;
    }
}

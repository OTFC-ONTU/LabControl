using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LabControl.Console.ViewModels;
using LabControl.Shared;

namespace LabControl.Console.Views;

/// <summary>
/// The single-PC window (ARCHITECTURE §8): the full picture of one PC and, with the Control
/// toggle on, the teacher's mouse and keyboard on it (M3 portion 3, D-36). The window only
/// turns Avalonia events into calls on the view model's <c>InputMapper</c>; pointer
/// positions are mapped through the picture's drawn bounds, so a window of any size and a
/// picture of any resolution agree on where the click landed.
/// </summary>
public partial class ScreenWindow : Window
{
    private readonly ScreenViewModel _viewModel;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _flush;
    private ScreenView? _picture;

    public ScreenWindow(ScreenViewModel viewModel)
    {
        _viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _viewModel.Tick());
        _flush = new DispatcherTimer(Defaults.InputFlushInterval, DispatcherPriority.Input, (_, _) => _viewModel.FlushInput());

        _picture = this.FindControl<ScreenView>("Picture");
        if (_picture is not null)
        {
            _picture.PointerMoved += OnPointerMoved;
            _picture.PointerPressed += OnPointerPressed;
            _picture.PointerReleased += OnPointerReleased;
            _picture.PointerWheelChanged += OnPointerWheel;
            _picture.KeyDown += OnKeyDown;
            _picture.KeyUp += OnKeyUp;
            _picture.TextInput += OnTextInput;
            _picture.LostFocus += (_, _) => _viewModel.ReleaseAll();
        }

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScreenViewModel.IsControlling))
            {
                OnControlChanged();
            }
        };

        viewModel.Activated += Activate;
        Deactivated += (_, _) => _viewModel.ReleaseAll();
        Opened += (_, _) =>
        {
            _viewModel.Open();
            _clock.Start();
        };
        Closed += (_, _) =>
        {
            _clock.Stop();
            _flush.Stop();
            viewModel.Activated -= Activate;
            _viewModel.Close();
        };
    }

    private void OnControlChanged()
    {
        if (_viewModel.IsControlling)
        {
            _flush.Start();
            _picture?.Focus();
        }
        else
        {
            _flush.Stop();
        }
    }

    // ------------------------------------------------------------------ pointer

    /// <summary>The pointer's place on the PC's screen, 0..1 each way; <c>null</c> when it is off the picture and not held.</summary>
    private (double X, double Y)? Normalise(PointerEventArgs e, bool evenOutside)
    {
        if (_picture is null || _picture.PictureBounds.Width <= 0)
        {
            return null;
        }

        var bounds = _picture.PictureBounds;
        var point = e.GetPosition(_picture);
        var x = (point.X - bounds.X) / bounds.Width;
        var y = (point.Y - bounds.Y) / bounds.Height;
        if (!evenOutside && (x < 0 || x > 1 || y < 0 || y > 1))
        {
            return null;
        }

        return (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_viewModel.IsControlling)
        {
            return;
        }

        // A drag that leaves the picture keeps reporting: the button is still down on the PC.
        var dragging = _viewModel.Mapper.PressedButtons.Count > 0;
        if (Normalise(e, evenOutside: dragging) is { } p)
        {
            _viewModel.Send(_viewModel.Mapper.PointerMoved(p.X, p.Y));
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_viewModel.IsControlling)
        {
            return;
        }

        _picture?.Focus();
        if (Normalise(e, evenOutside: false) is { } p)
        {
            e.Pointer.Capture(_picture);
            _viewModel.Send(_viewModel.Mapper.PointerButton(e.GetCurrentPoint(_picture).Properties.PointerUpdateKind, p.X, p.Y));
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_viewModel.IsControlling)
        {
            return;
        }

        if (Normalise(e, evenOutside: true) is { } p)
        {
            _viewModel.Send(_viewModel.Mapper.PointerButton(e.GetCurrentPoint(_picture).Properties.PointerUpdateKind, p.X, p.Y));
            e.Handled = true;
        }

        if (_viewModel.Mapper.PressedButtons.Count == 0)
        {
            e.Pointer.Capture(null);
        }
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_viewModel.IsControlling)
        {
            return;
        }

        if (Normalise(e, evenOutside: false) is { } p)
        {
            _viewModel.Send(_viewModel.Mapper.Wheel(e.Delta.X, e.Delta.Y, p.X, p.Y));
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_viewModel.IsControlling)
        {
            return;
        }

        _viewModel.Send(_viewModel.Mapper.KeyDown(e.PhysicalKey));
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_viewModel.IsControlling)
        {
            return;
        }

        _viewModel.Send(_viewModel.Mapper.KeyUp(e.PhysicalKey));
        e.Handled = true;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (!_viewModel.IsControlling)
        {
            return;
        }

        _viewModel.Send(_viewModel.Mapper.Text(e.Text));
        e.Handled = true;
    }
}

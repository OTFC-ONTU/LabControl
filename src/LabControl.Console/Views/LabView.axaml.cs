using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using LabControl.Console.Localization;
using LabControl.Console.ViewModels;

namespace LabControl.Console.Views;

/// <summary>
/// The room: one tile per PC on a canvas, in the layout from <c>lab.json</c>. Tiles are
/// created here rather than by an <c>ItemsControl</c> because dragging them into a new
/// cell is the whole point of the view (ARCHITECTURE §8), and that is simpler with the
/// controls in hand.
/// </summary>
public partial class LabView : UserControl
{
    private readonly Dictionary<MachineTileViewModel, ContentControl> _controls = [];
    private MainViewModel? _viewModel;
    private Canvas? _canvas;

    private MachineTileViewModel? _pressed;
    private ContentControl? _pressedControl;
    private Point _pressedAt;
    private Point _pressedOrigin;
    private bool _dragging;

    public LabView()
    {
        AvaloniaXamlLoader.Load(this);
        _canvas = this.FindControl<Canvas>("TileCanvas");
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        SizeChanged += (_, _) => _viewModel?.SetViewportWidth(Bounds.Width);
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Machines.CollectionChanged -= OnMachinesChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        _canvas?.Children.Clear();
        _controls.Clear();

        if (viewModel is null)
        {
            return;
        }

        viewModel.Machines.CollectionChanged += OnMachinesChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        foreach (var tile in viewModel.Machines)
        {
            Add(tile);
        }

        ApplyCanvasSize();
        viewModel.SetViewportWidth(Bounds.Width);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CanvasWidth) or nameof(MainViewModel.CanvasHeight)
            or nameof(MainViewModel.CellWidth) or nameof(MainViewModel.CellHeight))
        {
            ApplyCanvasSize();
        }
    }

    private void ApplyCanvasSize()
    {
        if (_canvas is null || _viewModel is null)
        {
            return;
        }

        _canvas.Width = _viewModel.CanvasWidth;
        _canvas.Height = _viewModel.CanvasHeight;
        foreach (var (_, control) in _controls)
        {
            control.Width = _viewModel.CellWidth;
            control.Height = _viewModel.CellHeight;
        }
    }

    private void OnMachinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _canvas?.Children.Clear();
            _controls.Clear();
            foreach (var tile in _viewModel!.Machines)
            {
                Add(tile);
            }

            return;
        }

        if (e.OldItems is not null)
        {
            foreach (MachineTileViewModel tile in e.OldItems)
            {
                if (_controls.Remove(tile, out var control))
                {
                    _canvas?.Children.Remove(control);
                    tile.PropertyChanged -= OnTileChanged;
                }
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MachineTileViewModel tile in e.NewItems)
            {
                Add(tile);
            }
        }
    }

    private void Add(MachineTileViewModel tile)
    {
        if (_canvas is null || _viewModel is null || _controls.ContainsKey(tile))
        {
            return;
        }

        var control = new ContentControl
        {
            Content = tile,
            ContentTemplate = (IDataTemplate)Resources["TileTemplate"]!,
            Width = _viewModel.CellWidth,
            Height = _viewModel.CellHeight,
            ContextMenu = BuildMenu(tile),
        };

        Canvas.SetLeft(control, tile.X);
        Canvas.SetTop(control, tile.Y);
        control.PointerPressed += OnTilePressed;
        control.PointerMoved += OnTileMoved;
        control.PointerReleased += OnTileReleased;
        tile.PropertyChanged += OnTileChanged;

        _controls[tile] = control;
        _canvas.Children.Add(control);
    }

    private ContextMenu BuildMenu(MachineTileViewModel tile)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            if (_viewModel is not null && !tile.IsSelected)
            {
                _viewModel.Select(tile, toggle: false);
            }
        };

        menu.Items.Add(new MenuItem { Header = Strings.Get("Action.OpenScreen"), Command = _viewModel!.OpenScreenCommand, CommandParameter = tile });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Strings.Get("Action.Shutdown"), Command = _viewModel.ShutdownCommand });
        menu.Items.Add(new MenuItem { Header = Strings.Get("Action.Reboot"), Command = _viewModel.RebootCommand });
        menu.Items.Add(new MenuItem { Header = Strings.Get("Action.Logoff"), Command = _viewModel.LogoffCommand });
        menu.Items.Add(new MenuItem { Header = Strings.Get("Action.RunScript"), Command = _viewModel.RunScriptCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Strings.Get("Machine.Remove"), Command = _viewModel.RemoveMachineCommand, CommandParameter = tile });
        menu.Items.Add(new MenuItem { Header = Strings.Get("Machine.Revoke"), Command = _viewModel.RevokeMachineCommand, CommandParameter = tile });
        return menu;
    }

    private void OnTileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MachineTileViewModel tile || !_controls.TryGetValue(tile, out var control))
        {
            return;
        }

        if (_dragging && ReferenceEquals(tile, _pressed))
        {
            return;
        }

        if (e.PropertyName == nameof(MachineTileViewModel.X))
        {
            Canvas.SetLeft(control, tile.X);
        }
        else if (e.PropertyName == nameof(MachineTileViewModel.Y))
        {
            Canvas.SetTop(control, tile.Y);
        }
    }

    // ------------------------------------------------------------------ click, select, drag

    private void OnTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ContentControl control || control.Content is not MachineTileViewModel tile || _canvas is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _pressed = null;
            _pressedControl = null;
            _viewModel?.OpenScreenCommand.Execute(tile);
            return;
        }

        _pressed = tile;
        _pressedControl = control;
        _pressedAt = e.GetPosition(_canvas);
        _pressedOrigin = new Point(Canvas.GetLeft(control), Canvas.GetTop(control));
        _dragging = false;
        e.Pointer.Capture(control);
    }

    private void OnTileMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || _pressedControl is null || _canvas is null)
        {
            return;
        }

        var position = e.GetPosition(_canvas);
        var delta = position - _pressedAt;
        if (!_dragging && (Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4))
        {
            _dragging = true;
            _pressedControl.ZIndex = 1;
            _pressedControl.Opacity = 0.85;
        }

        if (_dragging)
        {
            Canvas.SetLeft(_pressedControl, _pressedOrigin.X + delta.X);
            Canvas.SetTop(_pressedControl, _pressedOrigin.Y + delta.Y);
        }
    }

    private void OnTileReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is null || _pressedControl is null || _viewModel is null)
        {
            return;
        }

        e.Pointer.Capture(null);
        var tile = _pressed;
        var control = _pressedControl;
        _pressed = null;
        _pressedControl = null;

        if (_dragging)
        {
            _dragging = false;
            control.ZIndex = 0;
            control.Opacity = 1;

            var left = Canvas.GetLeft(control) + _viewModel.CellWidth / 2;
            var top = Canvas.GetTop(control) + _viewModel.CellHeight / 2;
            var column = (int)Math.Floor(left / _viewModel.CellWidth);
            var row = (int)Math.Floor(top / _viewModel.CellHeight);
            _viewModel.MoveTile(tile, column, row);
            Canvas.SetLeft(control, tile.X);
            Canvas.SetTop(control, tile.Y);
            return;
        }

        var toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        _viewModel.Select(tile, toggle);
    }
}

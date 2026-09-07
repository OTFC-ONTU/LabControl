using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaEdit;
using LabControl.Console.ViewModels;

namespace LabControl.Console.Views;

public partial class ScriptsView : UserControl
{
    private readonly TextEditor _editor;
    private readonly ScriptColorizer _colorizer = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ScriptsViewModel? _model;
    private bool _syncing;

    public ScriptsView()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<TextEditor>("Editor")!;
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.TextChanged += (_, _) =>
        {
            if (_syncing || _model is null) return;
            _model.Text = _editor.Text;
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _model?.ValidateText(); };
        DataContextChanged += (_, _) => Connect();
        AttachedToVisualTree += (_, _) => Connect();
        DetachedFromVisualTree += (_, _) =>
        {
            _debounce.Stop();
            if (_model is not null) _model.PropertyChanged -= ModelChanged;
            _model = null;
        };
    }

    private void Connect()
    {
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        _model = DataContext as ScriptsViewModel;
        if (_model is not null) _model.PropertyChanged += ModelChanged;
        Sync();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScriptsViewModel.Text) or nameof(ScriptsViewModel.Analysis)) Sync();
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var text = _model?.Text ?? string.Empty;
            if (_editor.Text != text)
            {
                _editor.Text = text;
                _editor.Document.UndoStack.ClearAll();
            }
            _colorizer.Tokens = _model?.Analysis.Tokens ?? [];
            _editor.TextArea.TextView.Redraw();
        }
        finally { _syncing = false; }
    }
}

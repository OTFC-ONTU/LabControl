using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;

namespace LabControl.Console.Views;

/// <summary>The first-run wizard window. Completes with the started lab, or <c>null</c> if dismissed.</summary>
public partial class SetupWindow : Window
{
    private readonly TaskCompletionSource<LabSession?> _completion = new();
    private readonly SetupViewModel _viewModel;

    public SetupWindow(ConsoleBootstrap bootstrap)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = new SetupViewModel(bootstrap, new WindowDialogs(this));
        DataContext = _viewModel;
        Closed += (_, _) => _completion.TrySetResult(_viewModel.IsDone ? _viewModel.Session : null);
    }

    public Task<LabSession?> Completion => _completion.Task;

    private void OnFinish(object? sender, RoutedEventArgs e)
    {
        _completion.TrySetResult(_viewModel.Session);
    }
}

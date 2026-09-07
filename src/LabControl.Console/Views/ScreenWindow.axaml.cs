using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LabControl.Console.ViewModels;

namespace LabControl.Console.Views;

/// <summary>The single-PC window (ARCHITECTURE §8): the full picture of one PC; input control comes in M3 portion 3.</summary>
public partial class ScreenWindow : Window
{
    private readonly ScreenViewModel _viewModel;
    private readonly DispatcherTimer _clock;

    public ScreenWindow(ScreenViewModel viewModel)
    {
        _viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _viewModel.Tick());

        viewModel.Activated += Activate;
        Opened += (_, _) =>
        {
            _viewModel.Open();
            _clock.Start();
        };
        Closed += (_, _) =>
        {
            _clock.Stop();
            viewModel.Activated -= Activate;
            _viewModel.Close();
        };
    }
}

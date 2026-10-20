using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared.Identity;

namespace LabControl.Console.Views;

public partial class MainWindow : Window, IDialogs
{
    private readonly WindowDialogs _dialogs;
    private readonly DispatcherTimer _clock;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _dialogs = new WindowDialogs(this);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => (DataContext as MainViewModel)?.Tick());
        Opened += (_, _) => _clock.Start();
        Closed += (_, _) => _clock.Stop();

        // Lab files dropped on the main window are added to the saved labs (M5 §5); nothing switches.
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) => e.DragEffects = DroppedFiles.PathsOf(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(this, (_, e) =>
        {
            var paths = DroppedFiles.PathsOf(e);
            if (paths.Count > 0)
            {
                FilesDropped?.Invoke(paths);
            }
        });
    }

    /// <summary>Local paths dropped on the window; the app imports them as saved labs.</summary>
    public event Action<IReadOnlyList<string>>? FilesDropped;

    private async void OnBannerAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is BannerViewModel { Action: { } action })
        {
            await action();
        }
    }

    // IDialogs: forwarded to the owner-bound implementation so the view model never sees Avalonia.

    public Task<UnlockAnswer?> UnlockAsync(string reason, bool offerRemember = false) => _dialogs.UnlockAsync(reason, offerRemember);

    public Task<HolderAnswer?> AddHolderAsync() => _dialogs.AddHolderAsync();

    public Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false) => _dialogs.AskTextAsync(title, prompt, initial, secret);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false) => _dialogs.ConfirmAsync(title, message, confirmLabel, destructive);

    public Task ShowMessageAsync(string title, string message) => _dialogs.ShowMessageAsync(title, message);

    public Task<bool> ShowRecoveryCodeAsync(RecoveryCode code) => _dialogs.ShowRecoveryCodeAsync(code);

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => _dialogs.PickSaveFileAsync(title, suggestedName, extension);

    public Task<string?> PickOpenFileAsync(string title, string extension) => _dialogs.PickOpenFileAsync(title, extension);

    public Task<string?> PickFolderAsync(string title) => _dialogs.PickFolderAsync(title);

    public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileFilter> filters) => _dialogs.PickOpenFilesAsync(title, filters);

    public Task ShowImportResultsAsync(IReadOnlyList<ImportFileResult> results) => _dialogs.ShowImportResultsAsync(results);

    public Task<SendFilesAnswer?> SendFilesAsync(int pcCount) => _dialogs.SendFilesAsync(pcCount);

    public Task<LockAnswer?> LockScreensAsync(int pcCount) => _dialogs.LockScreensAsync(pcCount);

    public Task<InternetAnswer?> InternetAsync(InternetDialogModel model) => _dialogs.InternetAsync(model);

    public Task<LabControl.Shared.Setup.AgentBuild?> PushAgentBuildAsync(int pcCount) => _dialogs.PushAgentBuildAsync(pcCount);

    public void ShowScreen(ScreenViewModel screen) => _dialogs.ShowScreen(screen);
}

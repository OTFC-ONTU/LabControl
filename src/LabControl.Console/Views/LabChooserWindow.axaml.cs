using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared.Identity;
using LabControl.Shared.Setup;

namespace LabControl.Console.Views;

/// <summary>
/// <i>My labs</i> (M5 §5): the window around <see cref="LabChooserViewModel"/>. A row opens
/// on double-click or Enter; files dropped anywhere on it are added as saved labs and
/// nothing switches. It is also its own <see cref="IDialogs"/>, forwarded to the
/// owner-bound implementation, so the view model never sees Avalonia.
/// </summary>
public partial class LabChooserWindow : Window, IDialogs
{
    private readonly WindowDialogs _dialogs;
    private LabChooserViewModel? _viewModel;

    public LabChooserWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _dialogs = new WindowDialogs(this);
        DataContextChanged += (_, _) => _viewModel = DataContext as LabChooserViewModel;

        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) => e.DragEffects = DroppedFiles.PathsOf(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(this, async (_, e) =>
        {
            var paths = DroppedFiles.PathsOf(e);
            if (paths.Count > 0 && _viewModel is { IsBusy: false } vm)
            {
                await vm.ImportFilesAsync(paths);
            }
        });
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is { } vm && vm.OpenCommand.CanExecute(vm.Selected))
        {
            vm.OpenCommand.Execute(vm.Selected);
        }
    }

    // IDialogs

    public Task<UnlockAnswer?> UnlockAsync(string reason) => _dialogs.UnlockAsync(reason);

    public Task<HolderAnswer?> AddHolderAsync() => _dialogs.AddHolderAsync();

    public Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false) => _dialogs.AskTextAsync(title, prompt, initial, secret);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false) => _dialogs.ConfirmAsync(title, message, confirmLabel, destructive);

    public Task ShowMessageAsync(string title, string message) => _dialogs.ShowMessageAsync(title, message);

    public Task<bool> ShowRecoveryCodeAsync(RecoveryCode code) => _dialogs.ShowRecoveryCodeAsync(code);

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => _dialogs.PickSaveFileAsync(title, suggestedName, extension);

    public Task<string?> PickOpenFileAsync(string title, string extension) => _dialogs.PickOpenFileAsync(title, extension);

    public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileFilter> filters) => _dialogs.PickOpenFilesAsync(title, filters);

    public Task ShowImportResultsAsync(IReadOnlyList<ImportFileResult> results) => _dialogs.ShowImportResultsAsync(results);

    public Task<string?> PickFolderAsync(string title) => _dialogs.PickFolderAsync(title);

    public Task<SendFilesAnswer?> SendFilesAsync(int pcCount) => _dialogs.SendFilesAsync(pcCount);

    public Task<AgentBuild?> PushAgentBuildAsync(int pcCount) => _dialogs.PushAgentBuildAsync(pcCount);

    public void ShowScreen(ScreenViewModel screen) => _dialogs.ShowScreen(screen);
}

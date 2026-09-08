using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared.Setup;
using LabControl.Shared.Identity;

namespace LabControl.Console.Views;

/// <summary>
/// <see cref="IDialogs"/> for any window: modal dialogs owned by it and the platform's
/// file pickers. Both the main window and the setup wizard use it.
/// </summary>
public sealed class WindowDialogs : IDialogs
{
    private readonly Window _owner;

    public WindowDialogs(Window owner) => _owner = owner;

    public async Task<UnlockAnswer?> UnlockAsync(string reason) =>
        await Show(new UnlockDialog(reason));

    public async Task<HolderAnswer?> AddHolderAsync() =>
        await Show(new HolderDialog());

    public async Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false) =>
        await Show(new TextDialog(title, prompt, initial, secret));

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false) =>
        await Show(new ConfirmDialog(title, message, confirmLabel, Strings.Get("Common.Cancel"), destructive)) == true;

    public async Task ShowMessageAsync(string title, string message) =>
        await Show(new ConfirmDialog(title, message, Strings.Get("Common.Ok"), null, destructive: false));

    public async Task<bool> ShowRecoveryCodeAsync(RecoveryCode code) =>
        await Show(new RecoveryCodeDialog(code)) == true;

    public async Task<AgentBuild?> PushAgentBuildAsync(int pcCount) =>
        await Show(new PushBuildDialog(pcCount));

    public async Task<SendFilesAnswer?> SendFilesAsync(int pcCount) =>
        await Show(new SendFilesDialog(pcCount));

    public void ShowScreen(ScreenViewModel screen) => new ScreenWindow(screen).Show();

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(Strings.Get("Backup.FileType")) { Patterns = ["*" + extension] }],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title, string extension)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Strings.Get("Backup.FileType")) { Patterns = ["*" + extension] }],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = filters.Select(f => new FilePickerFileType(f.Label) { Patterns = f.Patterns.ToList() }).ToList(),
        });

        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
    }

    public async Task ShowImportResultsAsync(IReadOnlyList<ImportFileResult> results) =>
        await Show(new ImportResultsDialog(results));

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private async Task<T?> Show<T>(DialogWindow<T> dialog)
    {
        await dialog.ShowDialog(_owner);
        return await dialog.Completion;
    }
}

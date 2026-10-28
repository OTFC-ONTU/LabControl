using LabControl.Console.Services;
using LabControl.Shared.Identity;
using LabControl.Shared.Setup;

namespace LabControl.Console.ViewModels;

/// <summary>What the teacher typed into the unlock dialog: one of the two, never both.</summary>
public sealed record UnlockAnswer(string? Passphrase, RecoveryCode? RecoveryCode, bool Remember = false);

/// <summary>The selected local handouts and the optional opening preference.</summary>
public sealed record SendFilesAnswer(IReadOnlyList<string> Paths, bool Open);

/// <summary>A new key holder from the add-holder dialog.</summary>
public sealed record HolderAnswer(string Name, string Passphrase);

/// <summary>What the teacher asked <i>Collect files…</i> for (M6 portion 5, D-77): the collection's name, the folder on the PCs, where it lands here.</summary>
public sealed record CollectFilesAnswer(string Name, string Folder, string Destination);

/// <summary>What the teacher asked the <i>Lock screens</i> dialog for (M6 portion 1): the message and how long, <c>null</c> meaning until lifted.</summary>
public sealed record LockAnswer(string Message, TimeSpan? Duration);


/// <summary>
/// The dialogs a view model needs, behind an interface so the view models stay free of
/// Avalonia and the window decides how each one looks. Every method returns <c>null</c>
/// (or <c>false</c>) when the teacher cancelled.
/// </summary>
public interface IDialogs
{
    /// <param name="offerRemember">Show "Remember on this computer", checked (D-82) — only for the session's own key.</param>
    Task<UnlockAnswer?> UnlockAsync(string reason, bool offerRemember = false);

    /// <param name="heading">Asks for a new key's first holder instead of an extra holder (D-82).</param>
    Task<HolderAnswer?> AddHolderAsync(string? heading = null, string? hint = null);

    Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false);

    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows the recovery code and returns once the teacher has acknowledged writing it down.</summary>
    Task<bool> ShowRecoveryCodeAsync(RecoveryCode code);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension);

    Task<string?> PickOpenFileAsync(string title, string extension);

    /// <summary>Several files at once (M5 <i>Add labs…</i>), filtered by extension; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileFilter> filters);

    /// <summary>The one-row-per-file outcome of an import batch (M5 §5).</summary>
    Task ShowImportResultsAsync(IReadOnlyList<ImportFileResult> results);

    Task<SendFilesAnswer?> SendFilesAsync(int pcCount);

    /// <summary>The <i>Lock screens</i> dialog (M6 portion 1, D-70): message and duration; the ceiling and the Ctrl+Alt+Del line are stated in it.</summary>
    Task<LockAnswer?> LockScreensAsync(int pcCount);

    /// <summary>The <i>Internet</i> dialog (M6 portion 3, D-73): mode, sites and presets, duration, and the refused names with their <i>Allow</i>.</summary>
    Task<InternetAnswer?> InternetAsync(InternetDialogModel model);

    /// <summary>The <i>Exam</i> dialog (M6 portion 4, D-75): the switches, the presets, and what the room must be told before <i>Start</i>.</summary>
    Task<Services.ExamRequest?> ExamAsync(ExamDialogModel model);

    /// <summary><i>Extend exam</i>: how many minutes more; <c>null</c> when cancelled.</summary>
    Task<TimeSpan?> ExtendExamAsync(int pcCount);

    Task<string?> PickFolderAsync(string title);

    /// <summary><i>Collect files…</i> (M6 portion 5, D-77); hosts without it collect nothing.</summary>
    Task<CollectFilesAnswer?> CollectFilesAsync(int pcCount) => Task.FromResult<CollectFilesAnswer?>(null);

    /// <summary><i>Reset profile…</i> (M6 portion 5, D-78): light or full, confirmed; <c>null</c> when cancelled.</summary>
    Task<LabControl.Shared.Jobs.ResetMode?> ResetProfileAsync(int pcCount) => Task.FromResult<LabControl.Shared.Jobs.ResetMode?>(null);

    /// <summary>The <i>Add package</i> wizard (M7 portion 3, D-88); the entry added, or <c>null</c>.</summary>
    Task<LabControl.Shared.Packages.PackageEntry?> AddPackageAsync(AddPackageModel model) => Task.FromResult<LabControl.Shared.Packages.PackageEntry?>(null);

    /// <summary>Shows a folder of this computer in the platform's file manager; hosts without one do nothing.</summary>
    Task OpenFolderAsync(string path) => Task.CompletedTask;

    /// <summary>The development-only <i>Push agent build</i> dialog (D-33): which folder, which version number; returns the build read from it.</summary>
    Task<AgentBuild?> PushAgentBuildAsync(int pcCount);

    /// <summary>Opens the single-PC screen window (ARCHITECTURE §8, M3); non-modal, the window tells the view model when it closes.</summary>
    void ShowScreen(ScreenViewModel screen);
}

using LabControl.Console.Services;
using LabControl.Shared.Identity;
using LabControl.Shared.Setup;

namespace LabControl.Console.ViewModels;

/// <summary>What the teacher typed into the unlock dialog: one of the two, never both.</summary>
public sealed record UnlockAnswer(string? Passphrase, RecoveryCode? RecoveryCode);

/// <summary>A new key holder from the add-holder dialog.</summary>
public sealed record HolderAnswer(string Name, string Passphrase);


/// <summary>
/// The dialogs a view model needs, behind an interface so the view models stay free of
/// Avalonia and the window decides how each one looks. Every method returns <c>null</c>
/// (or <c>false</c>) when the teacher cancelled.
/// </summary>
public interface IDialogs
{
    Task<UnlockAnswer?> UnlockAsync(string reason);

    Task<HolderAnswer?> AddHolderAsync();

    Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false);

    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows the recovery code and returns once the teacher has acknowledged writing it down.</summary>
    Task<bool> ShowRecoveryCodeAsync(RecoveryCode code);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension);

    Task<string?> PickOpenFileAsync(string title, string extension);

    Task<string?> PickFolderAsync(string title);

    /// <summary>The development-only <i>Run test script</i> dialog (D-31 item 3): which built-in script, which shell, as whom, how long.</summary>
    Task<TestScriptChoice?> RunTestScriptAsync(int pcCount);

    /// <summary>The development-only <i>Push agent build</i> dialog (D-33): which folder, which version number; returns the build read from it.</summary>
    Task<AgentBuild?> PushAgentBuildAsync(int pcCount);
}

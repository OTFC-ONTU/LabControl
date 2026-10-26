using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Localization;
using LabControl.Shared.Persistence;

namespace LabControl.Console.ViewModels;

/// <summary>
/// The language selector (M7, D-67 item 3, D-86), shown in <i>Settings</i> and in the lab
/// chooser so it can be reached before any lab exists. Choosing saves at once; the console
/// switches on its next start, and <i>Restart now</i> gets there through the ordinary quit.
/// </summary>
public sealed partial class LanguageChoiceViewModel : ObservableObject
{
    private readonly string _dataDirectory;
    private readonly Action<ProductLanguage> _save;

    public LanguageChoiceViewModel(string dataDirectory, ProductLanguage running, Action<ProductLanguage>? save = null)
    {
        _dataDirectory = dataDirectory;
        _save = save ?? (language => ConsoleLanguage.Save(_dataDirectory, language));
        Running = running;
        _selected = ConsoleLanguage.ReadStored(dataDirectory, out _) is { } stored && ProductLanguages.Find(stored) is { } choice ? choice : running;
    }

    /// <summary>Each language under its own name (D-67 item 3).</summary>
    public IReadOnlyList<ProductLanguage> Languages => ProductLanguages.All;

    /// <summary>The language this console is showing now.</summary>
    public ProductLanguage Running { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    private ProductLanguage _selected;

    /// <summary>Set when the choice could not be saved; the selector says why.</summary>
    [ObservableProperty]
    private string? _problem;

    /// <summary>A saved choice that differs from what is on screen.</summary>
    public bool NeedsRestart => !Equals(Selected, Running);

    /// <summary>Raised by <i>Restart now</i>; the app quits through the departure question and starts again.</summary>
    public event Action? RestartRequested;

    partial void OnSelectedChanged(ProductLanguage value)
    {
        try
        {
            _save(value);
            Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SchemaVersionException)
        {
            Problem = Strings.Format("Language.SaveFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke();
}

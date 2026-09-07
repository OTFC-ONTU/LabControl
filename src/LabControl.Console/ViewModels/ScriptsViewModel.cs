using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Jobs;
using LabControl.Shared.Persistence;

namespace LabControl.Console.ViewModels;

/// <summary>A script in the list on the left: name, one line, and whether the editor holds unsaved text for it.</summary>
public sealed partial class ScriptRowViewModel : ObservableObject
{
    public ScriptRowViewModel(ScriptRecord record) => Update(record);

    public string Id { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Details { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    public void Update(ScriptRecord record)
    {
        Id = record.Id;
        Name = record.Name;
        Description = record.Description;
        Details = string.Join(" · ",
            Strings.Get(record.ShellKind == ScriptShell.Cmd ? "Scripts.ShellCmd" : "Scripts.ShellPowerShell"),
            Strings.Get(record.RunAsKind == ScriptRunAs.User ? "Scripts.RunAsUser" : "Scripts.RunAsSystem"),
            Strings.Format("Scripts.TimeoutFormat", record.TimeoutSeconds));
    }
}

/// <summary>
/// The <i>Scripts</i> tab (D-31 items 4–5, D-38): the library on the left, the selected
/// script's fields and text on the right, edited in place. Unsaved edits are kept per
/// script while the teacher looks at another one, and <i>Run on selected PCs</i> sends
/// whatever the editor holds — saved or not — to the PCs selected in the lab view.
/// </summary>
public sealed partial class ScriptsViewModel : ObservableObject
{
    private readonly LabSession _session;
    private readonly IDialogs _dialogs;
    private readonly Func<IReadOnlyList<string>> _selectedAgents;
    private readonly Dictionary<string, ScriptRecord> _drafts = new(StringComparer.Ordinal);
    private ScriptRecord? _loaded;
    private bool _loading;

    [ObservableProperty]
    public partial string Validation { get; set; } = string.Empty;

    public ScriptAnalysis Analysis { get; private set; } = new([], []);

    public void ValidateText()
    {
        Analysis = ScriptAnalysis.Analyze(Text, ShellIndex == 1 ? ScriptShell.Cmd : ScriptShell.PowerShell);
        Validation = Analysis.Errors.Count > 0
            ? string.Join("\n", Analysis.Errors.Select(e => Strings.Format("Scripts.Diagnostic", e.Line, e.Column, e.Message)))
            : Strings.Get(ShellIndex == 1 ? "Scripts.CmdValidation" : "Scripts.ValidPowerShell");
        OnPropertyChanged(nameof(Analysis));
    }

    public ScriptsViewModel(LabSession session, IDialogs dialogs, Func<IReadOnlyList<string>> selectedAgents)
    {
        _session = session;
        _dialogs = dialogs;
        _selectedAgents = selectedAgents;
        session.Scripts.Changed += () => RefreshList();
        RefreshList();
        if (Scripts.Count > 0)
        {
            Selected = Scripts[0];
        }
    }

    public ObservableCollection<ScriptRowViewModel> Scripts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(RunCommand), nameof(SaveCommand), nameof(RevertCommand))]
    public partial ScriptRowViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>0 = PowerShell, 1 = cmd — the order of the combo box in the view.</summary>
    [ObservableProperty]
    public partial int ShellIndex { get; set; }

    /// <summary>0 = SYSTEM, 1 = the student's session.</summary>
    [ObservableProperty]
    public partial int RunAsIndex { get; set; }

    [ObservableProperty]
    public partial string TimeoutText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyPropertyChangedFor(nameof(RunLabel))]
    public partial int PcCount { get; set; }

    public string RunLabel => PcCount == 0 ? Strings.Get("Scripts.RunNone") : Strings.Format("Scripts.RunFormat", PcCount);

    /// <summary>The lab view tells us how many PCs are selected; the button says so.</summary>
    public void SetSelectedPcCount(int count) => PcCount = count;

    // ------------------------------------------------------------------ list

    private void RefreshList()
    {
        var records = _session.Scripts.Scripts;
        var keep = new HashSet<string>(records.Select(r => r.Id), StringComparer.Ordinal);

        for (var i = Scripts.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Scripts[i].Id))
            {
                _drafts.Remove(Scripts[i].Id);
                Scripts.RemoveAt(i);
            }
        }

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var existing = Scripts.FirstOrDefault(s => string.Equals(s.Id, record.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new ScriptRowViewModel(record);
                Scripts.Insert(Math.Min(i, Scripts.Count), existing);
            }
            else
            {
                existing.Update(record);
                var at = Scripts.IndexOf(existing);
                if (at != i && i < Scripts.Count)
                {
                    Scripts.Move(at, i);
                }
            }

            existing.IsDirty = _drafts.ContainsKey(record.Id);
        }

        if (Selected is not null && !keep.Contains(Selected.Id))
        {
            Selected = Scripts.FirstOrDefault();
        }
    }

    partial void OnSelectedChanged(ScriptRowViewModel? value)
    {
        StashDraft();
        HasSelection = value is not null;
        Error = string.Empty;
        Status = string.Empty;

        if (value is null)
        {
            _loaded = null;
            LoadFields(null);
            return;
        }

        _loaded = _session.Scripts.Find(value.Id);
        LoadFields(_drafts.TryGetValue(value.Id, out var draft) ? draft : _loaded);
    }

    /// <summary>Keeps the previous script's unsaved edits, so switching in the list loses nothing.</summary>
    private void StashDraft()
    {
        if (_loaded is null)
        {
            return;
        }

        var row = Scripts.FirstOrDefault(s => string.Equals(s.Id, _loaded.Id, StringComparison.Ordinal));
        if (IsDirty)
        {
            _drafts[_loaded.Id] = Collect(_loaded);
            if (row is not null)
            {
                row.IsDirty = true;
            }
        }
        else
        {
            _drafts.Remove(_loaded.Id);
            if (row is not null)
            {
                row.IsDirty = false;
            }
        }
    }

    private void LoadFields(ScriptRecord? record)
    {
        _loading = true;
        try
        {
            Name = record?.Name ?? string.Empty;
            Description = record?.Description ?? string.Empty;
            ShellIndex = record?.ShellKind == ScriptShell.Cmd ? 1 : 0;
            RunAsIndex = record?.RunAsKind == ScriptRunAs.User ? 1 : 0;
            TimeoutText = record is null ? string.Empty : record.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            Text = record?.Text ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }

        RecomputeDirty();
        ValidateText();
    }

    /// <summary>The editor's fields as a record, on top of the stored one's identity and dates.</summary>
    private ScriptRecord Collect(ScriptRecord basis)
    {
        var record = basis.Clone();
        record.Name = Name.Trim();
        record.Description = Description.Trim();
        record.Shell = ScriptRecord.ShellValue(ShellIndex == 1 ? ScriptShell.Cmd : ScriptShell.PowerShell);
        record.RunAs = ScriptRecord.RunAsValue(RunAsIndex == 1 ? ScriptRunAs.User : ScriptRunAs.System);
        record.TimeoutSeconds = int.TryParse(TimeoutText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        record.Text = Text;
        return record;
    }

    private void RecomputeDirty()
    {
        if (_loading)
        {
            return;
        }

        if (_loaded is null)
        {
            IsDirty = false;
            return;
        }

        var edited = Collect(_loaded);
        IsDirty = !string.Equals(edited.Name, _loaded.Name, StringComparison.Ordinal)
                  || !string.Equals(edited.Description, _loaded.Description, StringComparison.Ordinal)
                  || !string.Equals(edited.Shell, _loaded.Shell, StringComparison.Ordinal)
                  || !string.Equals(edited.RunAs, _loaded.RunAs, StringComparison.Ordinal)
                  || edited.TimeoutSeconds != _loaded.TimeoutSeconds
                  || !string.Equals(edited.Text, _loaded.Text, StringComparison.Ordinal);

        var row = Scripts.FirstOrDefault(s => string.Equals(s.Id, _loaded.Id, StringComparison.Ordinal));
        if (row is not null)
        {
            row.IsDirty = IsDirty;
        }
    }

    partial void OnNameChanged(string value) => RecomputeDirty();

    partial void OnDescriptionChanged(string value) => RecomputeDirty();

    partial void OnShellIndexChanged(int value)
    {
        RecomputeDirty();
        if (!_loading) ValidateText();
    }

    partial void OnRunAsIndexChanged(int value) => RecomputeDirty();

    partial void OnTimeoutTextChanged(string value) => RecomputeDirty();

    partial void OnTextChanged(string value) => RecomputeDirty();

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void AddBuiltIns()
    {
        var added = _session.Scripts.AddBuiltIns(SeedScripts.Embedded());
        Status = Strings.Format("Scripts.BuiltInsAdded", added);
    }

    [RelayCommand]
    private void New()
    {
        var record = _session.Scripts.NewScript();
        record.Text = Strings.Get("Scripts.NewTemplate");
        if (_session.Scripts.TrySave(record, out var error))
        {
            Selected = Scripts.FirstOrDefault(s => string.Equals(s.Id, record.Id, StringComparison.Ordinal));
        }
        else
        {
            Error = error;
        }
    }

    private bool CanSave() => Selected is not null && IsDirty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_loaded is null)
        {
            return;
        }

        var record = Collect(_loaded);
        if (!_session.Scripts.TrySave(record, out var error))
        {
            Error = error;
            return;
        }

        Error = string.Empty;
        _drafts.Remove(record.Id);
        _loaded = _session.Scripts.Find(record.Id);
        LoadFields(_loaded);
        Status = Strings.Format("Scripts.Saved", record.Name);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        if (_loaded is null)
        {
            return;
        }

        _drafts.Remove(_loaded.Id);
        LoadFields(_loaded);
        Error = string.Empty;
    }

    private bool CanDelete() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var id = Selected.Id;
        var name = Selected.Name;
        if (!await _dialogs.ConfirmAsync(name, Strings.Format("Scripts.DeleteConfirm", name), Strings.Get("Scripts.Delete"), destructive: true))
        {
            return;
        }

        _drafts.Remove(id);
        _loaded = null;
        _session.Scripts.Remove(id);
    }

    private bool CanRun() => Selected is not null && PcCount > 0;

    /// <summary>Sends the editor's current text — saved or not — to the PCs selected in the lab view.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (_loaded is null)
        {
            return;
        }

        var record = Collect(_loaded);
        if (record.Name.Length == 0)
        {
            Error = Strings.Get("Scripts.NeedName");
            return;
        }

        if (record.TimeoutSeconds <= 0)
        {
            Error = Strings.Get("Scripts.NeedTimeout");
            return;
        }

        if (record.Text.Trim().Length == 0)
        {
            Error = Strings.Get("Scripts.NeedText");
            return;
        }

        ValidateText();
        if (Analysis.Errors.Count > 0)
        {
            Error = Strings.Get("Scripts.FixErrors");
            return;
        }

        var targets = _selectedAgents();
        if (targets.Count == 0)
        {
            return;
        }

        Error = string.Empty;
        var jobs = _session.RunScript(targets, record);
        Status = Strings.Format(IsDirty ? "Scripts.RanUnsaved" : "Scripts.Ran", record.Name, jobs.Count);
    }
}

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Views;

/// <summary>
/// The <i>Exam</i> dialog (M6 portion 4, D-75, D-76): the four switches composed into one exam
/// — the message, the timer, the allowed programs and the internet (collecting work arrives in
/// portion 5) — with the named presets, and every plain sentence the room needs said before
/// <i>Start</i>: what happens to programs that are open, what happens when the time is up,
/// and that every PC ends the exam by itself after <see cref="Defaults.ExamHardLimit"/>.
/// </summary>
public sealed class ExamDialog : DialogWindow<ExamRequest>
{
    private readonly ExamDialogModel _model;
    private readonly ComboBox _presets = new() { MinWidth = 260 };
    private readonly TextBox _presetName = new() { MinWidth = 260 };
    private readonly TextBox _message = new() { AcceptsReturn = false, MinWidth = 380, MaxLength = 200 };
    private readonly RadioButton _noTimer = new() { GroupName = "timer" };
    private readonly RadioButton _timer = new() { GroupName = "timer", IsChecked = true };
    private readonly NumericUpDown _minutes;
    private readonly CheckBox _restrict = new();
    private readonly TextBox _programs = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinWidth = 380, MinHeight = 96, MaxHeight = 180 };
    private readonly RadioButton _internetUntouched = new() { GroupName = "internet", IsChecked = true };
    private readonly RadioButton _internetBlocked = new() { GroupName = "internet" };
    private readonly RadioButton _internetWhitelist = new() { GroupName = "internet" };
    private readonly ComboBox _internetPresets = new() { MinWidth = 220 };
    private readonly TextBlock _problem = new() { Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, IsVisible = false };
    private bool _choosing;

    public ExamDialog(ExamDialogModel model)
    {
        _model = model;
        Title = Strings.Get("Exam.Title");
        var ceilingMinutes = (int)Defaults.ExamHardLimit.TotalMinutes;
        _minutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = ceilingMinutes,
            Value = (decimal)Defaults.ExamDefaultTimer.TotalMinutes,
            Increment = 5,
            FormatString = "0",
            Width = 110,
        };

        _presetName.PlaceholderText = Strings.Get("Internet.PresetNamePlaceholder");
        _message.PlaceholderText = Strings.Get("Exam.MessagePlaceholder");
        _programs.PlaceholderText = Strings.Get("Exam.ProgramsPlaceholder");
        _noTimer.Content = Strings.Get("Exam.NoTimer");
        _timer.Content = Strings.Get("Exam.TimerMinutes");
        _restrict.Content = Strings.Get("Exam.RestrictPrograms");
        _internetUntouched.Content = Strings.Get("Exam.InternetUntouched");
        _internetBlocked.Content = Strings.Get("Internet.ModeBlocked");
        _internetWhitelist.Content = Strings.Get("Exam.InternetWhitelist");

        var savePreset = new Button { Content = Strings.Get("Internet.SavePreset") };
        var deletePreset = new Button { Content = Strings.Get("Internet.DeletePreset") };
        _presets.SelectionChanged += (_, _) => ChoosePreset();
        savePreset.Click += (_, _) => SavePreset();
        deletePreset.Click += (_, _) => DeletePreset();
        var presetRow = Row(_presets, deletePreset);
        var saveRow = Row(_presetName, savePreset);

        _timer.IsCheckedChanged += (_, _) => _minutes.IsEnabled = _timer.IsChecked == true;
        var timerRow = Row(_timer, _minutes);

        var programs = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(24, 0, 0, 0) };
        programs.Children.Add(_programs);
        programs.Children.Add(Label(Strings.Format("Exam.SweepWarning", (int)Defaults.ExamSweepGrace.TotalSeconds)));
        programs.Children.Add(Label(Strings.Get("Exam.RenameHole")));
        _restrict.IsCheckedChanged += (_, _) => programs.IsEnabled = _restrict.IsChecked == true;
        programs.IsEnabled = false;

        _internetPresets.ItemsSource = _model.InternetPresets.Select(p => new InternetItem(p)).ToList();
        _internetPresets.SelectedIndex = _model.InternetPresets.Count > 0 ? 0 : -1;
        _internetWhitelist.IsCheckedChanged += (_, _) => _internetPresets.IsEnabled = _internetWhitelist.IsChecked == true;
        _internetPresets.IsEnabled = false;
        var whitelistRow = Row(_internetWhitelist, _internetPresets);

        var start = Primary(Strings.Get("Exam.Start"));
        var cancel = Secondary(Strings.Get("Common.Cancel"));
        start.Click += (_, _) =>
        {
            if (Read() is { } request)
            {
                Finish(request);
            }
        };
        cancel.Click += (_, _) => Finish(null);

        var form = Body(
            Heading(Strings.Format(model.WholeLab ? "Exam.TargetsAll" : "Exam.Targets", model.PcCount)),
            presetRow,
            Label(Strings.Get("Exam.MessageLabel")),
            _message,
            _noTimer,
            timerRow,
            Label(Strings.Get("Exam.TimeUp")),
            _restrict,
            programs,
            Label(Strings.Get("Exam.InternetLabel")),
            _internetUntouched,
            _internetBlocked,
            whitelistRow,
            saveRow,
            Label(Strings.Format("Exam.Ceiling", (int)Defaults.ExamHardLimit.TotalHours)),
            Label(Strings.Get(model.WholeLab ? "Exam.LateJoin" : "Exam.Offline")),
            _problem);
        Content = null;
        var buttons = Buttons(cancel, start);
        buttons.Margin = new Avalonia.Thickness(20, 8, 20, 16);
        DockPanel.SetDock(buttons, Dock.Bottom);
        var layout = new DockPanel();
        layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = layout;
        Opened += (_, _) =>
        {
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
            {
                MaxHeight = screen.WorkingArea.Height / screen.Scaling * 0.9;
            }
        };

        FillPresets(null);
    }

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private sealed record PresetItem(ExamPreset? Preset)
    {
        public override string ToString() => Preset?.Name ?? Strings.Get("Exam.Unsaved");
    }

    private sealed record InternetItem(InternetPreset Preset)
    {
        public override string ToString() => Preset.Name;
    }

    /// <summary>What the dialog says now, or <c>null</c> with the problem shown.</summary>
    private ExamRequest? Read()
    {
        var (programs, problem) = ExamDialogModel.ParsePrograms(_programs.Text);
        if (_restrict.IsChecked == true && problem is not null)
        {
            Show(problem);
            return null;
        }

        var mode = _internetBlocked.IsChecked == true ? InternetPolicy.Types.Mode.Blocked
            : _internetWhitelist.IsChecked == true ? InternetPolicy.Types.Mode.Whitelist
            : InternetPolicy.Types.Mode.Open;
        IReadOnlyList<string> hosts = [];
        var internetName = string.Empty;
        if (mode == InternetPolicy.Types.Mode.Whitelist)
        {
            if (_internetPresets.SelectedItem is not InternetItem { Preset: { } internet } || internet.Hosts.Count == 0)
            {
                Show(Strings.Get("Exam.NoInternetPreset"));
                return null;
            }

            hosts = internet.Hosts;
            internetName = internet.Name;
        }

        TimeSpan? timer = _timer.IsChecked == true && _minutes.Value is { } value
            ? TimeSpan.FromMinutes((double)Math.Clamp(value, 1, (decimal)Defaults.ExamHardLimit.TotalMinutes))
            : null;
        var presetName = _presets.SelectedItem is PresetItem { Preset: { } preset } ? preset.Name : string.Empty;
        _problem.IsVisible = false;
        return new ExamRequest((_message.Text ?? string.Empty).Trim(), timer, _restrict.IsChecked == true,
            _restrict.IsChecked == true ? programs : [], mode, hosts, internetName, presetName);
    }

    private void FillPresets(string? selectId)
    {
        _choosing = true;
        var items = new List<PresetItem> { new(null) };
        items.AddRange(_model.Presets.Select(p => new PresetItem(p)));
        _presets.ItemsSource = items;
        _presets.SelectedItem = selectId is null
            ? items.Count > 1 ? items[1] : items[0]
            : items.FirstOrDefault(i => i.Preset is { } p && p.Id == selectId) ?? items[0];
        _choosing = false;
        ChoosePreset();
    }

    private void ChoosePreset()
    {
        if (_choosing || _presets.SelectedItem is not PresetItem { Preset: { } preset })
        {
            return;
        }

        _presetName.Text = preset.Name;
        _message.Text = preset.Message;
        _noTimer.IsChecked = preset.TimerMinutes <= 0;
        _timer.IsChecked = preset.TimerMinutes > 0;
        if (preset.TimerMinutes > 0)
        {
            _minutes.Value = Math.Clamp(preset.TimerMinutes, 1, (int)Defaults.ExamHardLimit.TotalMinutes);
        }

        _restrict.IsChecked = preset.RestrictPrograms;
        _programs.Text = string.Join("\n", preset.AllowedPrograms);
        _internetUntouched.IsChecked = preset.InternetMode == ExamInternetModes.Untouched;
        _internetBlocked.IsChecked = preset.InternetMode == ExamInternetModes.Blocked;
        _internetWhitelist.IsChecked = preset.InternetMode == ExamInternetModes.Whitelist;
        if (preset.InternetMode == ExamInternetModes.Whitelist && _internetPresets.ItemsSource is IEnumerable<InternetItem> internet)
        {
            _internetPresets.SelectedItem = internet.FirstOrDefault(i => string.Equals(i.Preset.Name, preset.InternetPresetName, StringComparison.OrdinalIgnoreCase))
                                            ?? internet.FirstOrDefault();
        }
    }

    private void SavePreset()
    {
        if (Read() is not { } request)
        {
            return;
        }

        var name = _presetName.Text ?? string.Empty;
        var id = _presets.SelectedItem is PresetItem { Preset: { } current } && string.Equals(current.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)
            ? current.Id
            : null;
        var (saved, problem) = _model.SavePreset(id, ExamDialogModel.ToPreset(name, request));
        if (problem is not null)
        {
            Show(problem);
            return;
        }

        FillPresets(saved?.Id);
    }

    private void DeletePreset()
    {
        if (_presets.SelectedItem is PresetItem { Preset: { } preset } && _model.DeletePreset(preset.Id))
        {
            FillPresets(string.Empty);
        }
    }

    private void Show(string problem)
    {
        _problem.Text = problem;
        _problem.IsVisible = true;
    }
}

/// <summary>
/// <i>Extend exam</i> (M6 portion 4): one click adds a few minutes to every PC's timer, counted
/// from what each PC has left.
/// </summary>
public sealed class ExtendExamDialog : DialogWindow<TimeSpan?>
{
    public ExtendExamDialog(int pcCount)
    {
        Title = Strings.Get("Exam.ExtendTitle");
        var buttons = new List<Control>();
        foreach (var minutes in new[] { 5, 10, 15 })
        {
            var button = new Button { Content = Strings.Format("Exam.ExtendBy", minutes) };
            button.Click += (_, _) => Finish(TimeSpan.FromMinutes(minutes));
            buttons.Add(button);
        }

        var cancel = Secondary(Strings.Get("Common.Cancel"));
        cancel.Click += (_, _) => Finish(null);
        buttons.Insert(0, cancel);
        Body(
            Heading(Strings.Format("Exam.ExtendTargets", pcCount)),
            Label(Strings.Get("Exam.ExtendNote")),
            Buttons([.. buttons]));
    }
}

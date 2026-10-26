using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LabControl.Console.Localization;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Views;

/// <summary>
/// The <i>Internet</i> dialog (M6 portion 3, D-73): <i>open</i>, <i>only these sites</i> or
/// <i>no internet</i>; the site list with its named presets; how long — this lesson, a number
/// of minutes, or until the teacher lifts it — with the ceiling every PC enforces on its own;
/// and the names the PCs' whitelists refused lately, each with an <i>Allow</i> that acts at once
/// (owner decision 2026-10-17, D-74). The refused list refreshes while the dialog is open.
/// </summary>
public sealed class InternetDialog : DialogWindow<InternetAnswer>
{
    private readonly InternetDialogModel _model;
    private readonly ComboBox _presets = new() { MinWidth = 220 };
    private readonly TextBox _hosts = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        MinWidth = 380,
        MinHeight = 96,
        MaxHeight = 180,
    };

    private readonly TextBox _presetName = new() { MinWidth = 220 };
    private readonly TextBlock _problem = new() { Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, IsVisible = false };
    private readonly StackPanel _refused = new() { Spacing = 4 };
    private readonly DispatcherTimer _refresh = new() { Interval = Defaults.InternetStateRefusedInterval };
    private bool _choosing;

    public InternetDialog(InternetDialogModel model)
    {
        _model = model;
        Title = Strings.Get("Internet.Title");
        var ceilingHours = (int)Defaults.InternetPolicyHardLimit.TotalHours;
        var lessonMinutes = (int)Defaults.InternetPolicyLessonDuration.TotalMinutes;

        var open = new RadioButton { Content = Strings.Get("Internet.ModeOpen"), GroupName = "mode" };
        var whitelist = new RadioButton { Content = Strings.Get("Internet.ModeWhitelist"), GroupName = "mode", IsChecked = true };
        var blocked = new RadioButton { Content = Strings.Get("Internet.ModeBlocked"), GroupName = "mode" };

        _hosts.PlaceholderText = Strings.Get("Internet.HostsPlaceholder");
        _presetName.PlaceholderText = Strings.Get("Internet.PresetNamePlaceholder");
        var savePreset = new Button { Content = Strings.Get("Internet.SavePreset") };
        var deletePreset = new Button { Content = Strings.Get("Internet.DeletePreset") };
        _presets.SelectionChanged += (_, _) => ChoosePreset();
        savePreset.Click += (_, _) => SavePreset();
        deletePreset.Click += (_, _) => DeletePreset();
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        presetRow.Children.Add(_presets);
        presetRow.Children.Add(deletePreset);
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        saveRow.Children.Add(_presetName);
        saveRow.Children.Add(savePreset);
        var sites = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(24, 0, 0, 0) };
        sites.Children.Add(presetRow);
        sites.Children.Add(_hosts);
        sites.Children.Add(saveRow);
        sites.Children.Add(Label(Strings.Get("Internet.HostsOnly")));
        whitelist.IsCheckedChanged += (_, _) => sites.IsEnabled = whitelist.IsChecked == true;

        var lesson = new RadioButton { Content = Strings.Format("Internet.ForLesson", lessonMinutes), GroupName = "duration", IsChecked = true };
        var forMinutes = new RadioButton { Content = Strings.Get("Lock.ForMinutes"), GroupName = "duration" };
        var untilLifted = new RadioButton { Content = Strings.Get("Internet.UntilLifted"), GroupName = "duration" };
        var minutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = ceilingHours * 60,
            Value = 45,
            Increment = 5,
            FormatString = "0",
            Width = 110,
            IsEnabled = false,
        };
        forMinutes.IsCheckedChanged += (_, _) => minutes.IsEnabled = forMinutes.IsChecked == true;
        var minutesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        minutesRow.Children.Add(forMinutes);
        minutesRow.Children.Add(minutes);
        var duration = new StackPanel { Spacing = 4 };
        duration.Children.Add(lesson);
        duration.Children.Add(minutesRow);
        duration.Children.Add(untilLifted);
        duration.Children.Add(Label(Strings.Format("Internet.Ceiling", ceilingHours)));
        open.IsCheckedChanged += (_, _) => duration.IsEnabled = open.IsChecked != true;

        var apply = Primary(Strings.Get("Internet.Apply"));
        var cancel = Secondary(Strings.Get("Common.Cancel"));
        apply.Click += (_, _) =>
        {
            if (open.IsChecked == true)
            {
                Finish(new InternetAnswer(InternetPolicy.Types.Mode.Open, [], string.Empty, null));
                return;
            }

            TimeSpan? chosen = lesson.IsChecked == true ? Defaults.InternetPolicyLessonDuration
                : forMinutes.IsChecked == true && minutes.Value is { } value ? TimeSpan.FromMinutes((double)Math.Clamp(value, 1, ceilingHours * 60))
                : null;
            if (blocked.IsChecked == true)
            {
                Finish(new InternetAnswer(InternetPolicy.Types.Mode.Blocked, [], string.Empty, chosen));
                return;
            }

            var (hosts, problem) = InternetDialogModel.ParseHosts(_hosts.Text);
            if (problem is not null)
            {
                Show(problem);
                return;
            }

            var preset = _presets.SelectedItem is PresetItem { Preset: { } p } && p.Hosts.SequenceEqual(hosts, StringComparer.Ordinal) ? p.Name : string.Empty;
            Finish(new InternetAnswer(InternetPolicy.Types.Mode.Whitelist, hosts, preset, chosen));
        };
        cancel.Click += (_, _) => Finish(null);

        var refusedBox = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(8),
            Child = new ScrollViewer { MaxHeight = 120, Content = _refused },
        };

        // The buttons stay at the bottom, always visible; everything above scrolls. Sized to its
        // content the dialog grew past the screen on a 13" Mac as the refused list filled, and
        // Apply ended up under the Dock (found in the lab, 2026-10-20).
        var form = Body(
            Heading(Strings.Format("Internet.Targets", model.PcCount)),
            ExamWarning(model.ExamPcs),
            open,
            whitelist,
            sites,
            blocked,
            Label(Strings.Get("Internet.BlockedKeepsLab")),
            duration,
            Label(Strings.Get("Internet.RefusedHeading")),
            refusedBox,
            _problem,
            Label(Strings.Get("Internet.Offline")));
        Content = null;
        var buttons = Buttons(cancel, apply);
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
        RefreshRefused();
        _refresh.Tick += (_, _) => RefreshRefused();
        _refresh.Start();
        Closed += (_, _) => _refresh.Stop();
    }

    /// <summary>The exam's internet switch wins on these PCs until the exam ends; nothing when there are none.</summary>
    private static Control ExamWarning(IReadOnlyList<int> examPcs) => new TextBlock
    {
        Text = examPcs.Count == 0 ? string.Empty : Strings.Format("Internet.ExamWins",
            string.Join(", ", examPcs.Select(n => string.Format(Strings.Culture, Defaults.MachineNameFormat, n)))),
        Foreground = Brushes.DarkOrange,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 460,
        IsVisible = examPcs.Count > 0,
    };

    /// <summary>One entry of the preset list: a saved preset, or the unsaved list being typed.</summary>
    private sealed record PresetItem(InternetPreset? Preset)
    {
        public override string ToString() => Preset?.Name ?? Strings.Get("Internet.UnsavedList");
    }

    private void FillPresets(string? selectId)
    {
        _choosing = true;
        var items = new List<PresetItem> { new(null) };
        items.AddRange(_model.Presets.Select(p => new PresetItem(p)));
        _presets.ItemsSource = items;
        // null = the first saved preset (the dialog opening); an id that is gone = the unsaved list.
        _presets.SelectedItem = selectId is null
            ? items.Count > 1 ? items[1] : items[0]
            : items.FirstOrDefault(i => i.Preset is { } p && p.Id == selectId) ?? items[0];
        _choosing = false;
        ChoosePreset();
    }

    private void ChoosePreset()
    {
        if (_choosing)
        {
            return;
        }

        if (_presets.SelectedItem is PresetItem { Preset: { } preset })
        {
            _hosts.Text = string.Join("\n", preset.Hosts);
            _presetName.Text = preset.Name;
        }
    }

    private void SavePreset()
    {
        var id = _presets.SelectedItem is PresetItem { Preset: { } current } && string.Equals(current.Name, _presetName.Text?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? current.Id
            : null;
        var (saved, problem) = _model.SavePreset(id, _presetName.Text ?? string.Empty, (_hosts.Text ?? string.Empty).Split('\n'));
        if (problem is not null)
        {
            Show(problem);
            return;
        }

        _problem.IsVisible = false;
        FillPresets(saved?.Id);
    }

    private void DeletePreset()
    {
        if (_presets.SelectedItem is PresetItem { Preset: { } preset } && _model.DeletePreset(preset.Id))
        {
            FillPresets(string.Empty);
        }
    }

    /// <summary>Redraws the refused names; an <i>Allow</i> asks the PCs at once and adds the name to the list being edited.</summary>
    private void RefreshRefused()
    {
        _refused.Children.Clear();
        var names = _model.Refused();
        if (names.Count == 0)
        {
            _refused.Children.Add(new TextBlock { Text = Strings.Get("Internet.NothingRefused"), Opacity = 0.7 });
            return;
        }

        foreach (var name in names)
        {
            var row = new DockPanel { LastChildFill = true };
            var allow = new Button { Content = Strings.Get("Internet.Allow"), Margin = new Avalonia.Thickness(8, 0, 0, 0) };
            allow.Click += (_, _) =>
            {
                _model.Allow(name.Name);
                var text = _hosts.Text ?? string.Empty;
                if (!text.Split('\n', StringSplitOptions.TrimEntries).Contains(name.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _hosts.Text = text.Length == 0 || text.EndsWith('\n') ? text + name.Name : text + "\n" + name.Name;
                }

                allow.IsEnabled = false;
            };
            DockPanel.SetDock(allow, Dock.Right);
            row.Children.Add(allow);
            row.Children.Add(new TextBlock
            {
                Text = Strings.Format("Internet.RefusedRow", name.Name, name.PcCount),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            _refused.Children.Add(row);
        }
    }

    private void Show(string problem)
    {
        _problem.Text = problem;
        _problem.IsVisible = true;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Jobs;
using LabControl.Shared.Setup;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LabControl.Console.Views;

/// <summary>
/// The console's small dialogs, built in code: each is a few controls and a result, and a
/// XAML file per dialog would be more ceremony than content. Every string comes from
/// <see cref="Strings"/>.
/// </summary>
public abstract class DialogWindow<T> : Window
{
    private readonly TaskCompletionSource<T?> _completion = new();

    protected DialogWindow()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        MinWidth = 420;
        Closed += (_, _) => _completion.TrySetResult(default);
    }

    /// <summary>Completes when the dialog closes, with its answer or <c>null</c>.</summary>
    public Task<T?> Completion => _completion.Task;

    protected void Finish(T? result)
    {
        _completion.TrySetResult(result);
        Close();
    }

    protected static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };

    protected static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };

    protected static TextBox Field(string watermark, bool secret = false) => new()
    {
        PlaceholderText = watermark,
        PasswordChar = secret ? '•' : '\0',
        MinWidth = 380,
    };

    protected static StackPanel Buttons(params Control[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    protected static Button Primary(string text) => new() { Content = text, IsDefault = true, Classes = { "accent" } };

    protected static Button Secondary(string text) => new() { Content = text, IsCancel = true };

    protected StackPanel Body(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        Content = panel;
        return panel;
    }
}

public sealed class UnlockDialog : DialogWindow<UnlockAnswer>
{
    public UnlockDialog(string reason)
    {
        Title = Strings.Get("Unlock.Title");

        var passphrase = Field(Strings.Get("Unlock.Passphrase"), secret: true);
        var recovery = Field(Strings.Get("Unlock.RecoveryCode"));
        var useRecovery = new CheckBox { Content = Strings.Get("Unlock.UseRecoveryCode") };
        recovery.IsVisible = false;
        useRecovery.IsCheckedChanged += (_, _) =>
        {
            var byCode = useRecovery.IsChecked == true;
            recovery.IsVisible = byCode;
            passphrase.IsVisible = !byCode;
        };

        var error = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };

        var ok = Primary(Strings.Get("Unlock.Action"));
        ok.Click += (_, _) =>
        {
            if (useRecovery.IsChecked == true)
            {
                if (!RecoveryCode.TryParse(recovery.Text, out var code))
                {
                    error.Text = Strings.Get("Setup.BadRecoveryCode");
                    error.IsVisible = true;
                    return;
                }

                Finish(new UnlockAnswer(null, code));
                return;
            }

            if (string.IsNullOrEmpty(passphrase.Text))
            {
                error.Text = Strings.Get("Unlock.NeedPassphrase");
                error.IsVisible = true;
                return;
            }

            Finish(new UnlockAnswer(passphrase.Text, null));
        };

        var cancel = Secondary(Strings.Get("Common.Cancel"));
        cancel.Click += (_, _) => Finish(null);

        Body(Heading(Strings.Get("Unlock.Heading")), Label(reason), passphrase, recovery, useRecovery, error, Buttons(cancel, ok));
        Opened += (_, _) => passphrase.Focus();
    }
}

public sealed class HolderDialog : DialogWindow<HolderAnswer>
{
    public HolderDialog()
    {
        Title = Strings.Get("Key.AddHolder");

        var name = Field(Strings.Get("Key.HolderName"));
        var passphrase = Field(Strings.Get("Key.HolderPassphrase"), secret: true);
        var again = Field(Strings.Get("Key.HolderPassphraseAgain"), secret: true);
        var error = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };

        var ok = Primary(Strings.Get("Key.AddHolder"));
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                error.Text = Strings.Get("Setup.NeedHolderName");
            }
            else if ((passphrase.Text ?? string.Empty).Length < 8)
            {
                error.Text = Strings.Get("Setup.PassphraseTooShort");
            }
            else if (!string.Equals(passphrase.Text, again.Text, StringComparison.Ordinal))
            {
                error.Text = Strings.Get("Setup.PassphraseMismatch");
            }
            else
            {
                Finish(new HolderAnswer(name.Text.Trim(), passphrase.Text!));
                return;
            }

            error.IsVisible = true;
        };

        var cancel = Secondary(Strings.Get("Common.Cancel"));
        cancel.Click += (_, _) => Finish(null);

        Body(Heading(Strings.Get("Key.AddHolder")), Label(Strings.Get("Key.AddHolderHint")), name, passphrase, again, error, Buttons(cancel, ok));
        Opened += (_, _) => name.Focus();
    }
}

public sealed class TextDialog : DialogWindow<string>
{
    public TextDialog(string title, string prompt, string initial, bool secret)
    {
        Title = title;
        var field = Field(string.Empty, secret);
        field.Text = initial;

        var ok = Primary(Strings.Get("Common.Ok"));
        ok.Click += (_, _) => Finish(field.Text ?? string.Empty);
        var cancel = Secondary(Strings.Get("Common.Cancel"));
        cancel.Click += (_, _) => Finish(null);

        Body(Heading(title), Label(prompt), field, Buttons(cancel, ok));
        Opened += (_, _) => field.Focus();
    }
}

public sealed class ConfirmDialog : DialogWindow<bool?>
{
    public ConfirmDialog(string title, string message, string confirmLabel, string? cancelLabel, bool destructive)
    {
        Title = title;

        var ok = Primary(confirmLabel);
        if (destructive)
        {
            ok.Classes.Remove("accent");
            ok.Classes.Add("danger");
        }

        ok.Click += (_, _) => Finish(true);

        var buttons = new List<Control>();
        if (cancelLabel is not null)
        {
            var cancel = Secondary(cancelLabel);
            cancel.Click += (_, _) => Finish(false);
            buttons.Add(cancel);
        }

        buttons.Add(ok);
        Body(Heading(title), Label(message), Buttons(buttons.ToArray()));
    }
}

public sealed class RecoveryCodeDialog : DialogWindow<bool?>
{
    public RecoveryCodeDialog(RecoveryCode code)
    {
        Title = Strings.Get("Settings.RecoveryCode");

        var text = new TextBox
        {
            Text = code.ToPrintableString(),
            IsReadOnly = true,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = 18,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var acknowledged = new CheckBox { Content = Strings.Get("Setup.AcknowledgeRecovery") };
        var ok = Primary(Strings.Get("Common.Done"));
        ok.IsEnabled = false;
        acknowledged.IsCheckedChanged += (_, _) => ok.IsEnabled = acknowledged.IsChecked == true;
        ok.Click += (_, _) => Finish(true);

        Body(Heading(Strings.Get("Setup.RecoveryHeading")), Label(Strings.Get("Setup.RecoveryBody")), text, acknowledged, Buttons(ok));
    }
}

/// <summary>
/// The development-only <i>Push agent build</i> dialog (ROADMAP M2 portion 4, D-33): a
/// folder with the published <c>agent.exe</c> and <c>session.exe</c>, and the version number
/// they were built with. The folder is read and hashed as soon as it is named, so the
/// teacher sees what would be sent — and its version directory name — before pushing.
/// </summary>
public sealed class PushBuildDialog : DialogWindow<AgentBuild>
{
    private AgentBuild? _build;
    private int _generation;

    public PushBuildDialog(int pcCount)
    {
        Title = Strings.Get("Action.PushBuild");

        var folder = Field(Strings.Get("Push.Folder"));
        var browse = new Button { Content = Strings.Get("Push.Browse") };
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderRow.Children.Add(folder);
        folderRow.Children.Add(browse);

        var version = Field(Strings.Get("Push.Version"));
        version.Text = typeof(PushBuildDialog).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var summary = new TextBlock { Text = Strings.Get("Push.NothingYet"), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };

        var ok = Primary(Strings.Get("Push.Send"));
        ok.IsEnabled = false;
        ok.Click += (_, _) =>
        {
            if (_build is not null)
            {
                Finish(_build);
            }
        };

        var cancel = Secondary(Strings.Get("Common.Cancel"));
        cancel.Click += (_, _) => Finish(null);

        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("Push.PickFolder"), AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            {
                folder.Text = path;
            }
        };

        folder.TextChanged += (_, _) => Reload();
        version.TextChanged += (_, _) => Reload();

        Body(
            Heading(Strings.Format("Push.Heading", pcCount)),
            Label(Strings.Get("Push.Hint")),
            Label(Strings.Get("Push.Folder")), folderRow,
            Label(Strings.Get("Push.Version")), version,
            summary,
            Buttons(cancel, ok));

        // Hashing two large executables takes a moment; it happens off the UI thread and the
        // latest request wins.
        void Reload()
        {
            var generation = ++_generation;
            var folderText = folder.Text ?? string.Empty;
            var versionText = version.Text ?? string.Empty;
            _build = null;
            ok.IsEnabled = false;

            if (string.IsNullOrWhiteSpace(folderText))
            {
                summary.Text = Strings.Get("Push.NothingYet");
                summary.Foreground = null;
                return;
            }

            _ = Task.Run(() =>
            {
                var loaded = AgentBuild.TryLoad(folderText, versionText, out var build, out var error);
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    if (loaded)
                    {
                        _build = build;
                        summary.Text = build.Describe();
                        summary.Foreground = null;
                        ok.IsEnabled = true;
                    }
                    else
                    {
                        summary.Text = error;
                        summary.Foreground = Brushes.IndianRed;
                    }
                });
            });
        }
    }
}

/// <summary>One row per imported file (M5 §5): what became a saved lab and what did not, and why.</summary>
public sealed class ImportResultsDialog : DialogWindow<bool?>
{
    public ImportResultsDialog(IReadOnlyList<ImportFileResult> results)
    {
        Title = Strings.Get("Import.ResultsTitle");

        var rows = new StackPanel { Spacing = 6 };
        foreach (var result in results)
        {
            var row = new DockPanel { LastChildFill = true };
            var mark = new TextBlock
            {
                Text = Strings.Get(result.Ok ? "Import.MarkAdded" : "Import.MarkFailed"),
                Foreground = result.Ok ? Brushes.SeaGreen : Brushes.IndianRed,
                FontWeight = FontWeight.Bold,
                Width = 20,
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = result.FileName, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
            text.Children.Add(new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Opacity = 0.85 });
            row.Children.Add(text);
            rows.Children.Add(row);
        }

        var added = results.Count(r => r.Ok);
        var ok = Primary(Strings.Get("Common.Ok"));
        ok.Click += (_, _) => Finish(true);

        Body(
            Heading(Strings.Format("Import.ResultsHeading", added, results.Count)),
            new ScrollViewer { Content = rows, MaxHeight = 360 },
            Label(Strings.Get("Import.NothingOpened")),
            Buttons(ok));
    }
}

/// <summary>
/// <i>Leave {lab}?</i> — the <see cref="DepartureReport"/> in words: every running job with
/// its fate, queued jobs, uploads, probations and wakes; then <i>Stay</i>, <i>Wait for N
/// jobs</i> (only when jobs are running) and <i>Leave anyway</i>.
/// </summary>
public sealed class DepartureDialog : DialogWindow<DepartureChoice?>
{
    public DepartureDialog(string labName, DepartureReport report)
    {
        Title = Strings.Format("Departure.Title", labName);

        var lines = new StackPanel { Spacing = 4 };
        foreach (var group in report.RunningJobs)
        {
            var kind = Strings.Get("Job." + group.Kind);
            var key = group.Consequence switch
            {
                DepartureConsequence.Completes => "Departure.Jobs.Completes",
                DepartureConsequence.CannotBeAborted => "Departure.Jobs.CannotAbort",
                _ => "Departure.Jobs.ContinuesOnPc",
            };
            lines.Children.Add(Label(Strings.Get("Common.Bullet") + Strings.Format(key, group.Count, kind)));
        }

        if (report.QueuedJobs > 0)
        {
            lines.Children.Add(Label(Strings.Get("Common.Bullet") + Strings.Format("Departure.Queued", report.QueuedJobs)));
        }

        if (report.UploadsInProgress > 0)
        {
            lines.Children.Add(Label(Strings.Get("Common.Bullet") + Strings.Format("Departure.Uploads", report.UploadsInProgress)));
        }

        if (report.ProbationPcs.Count > 0)
        {
            lines.Children.Add(Label(Strings.Get("Common.Bullet") + Strings.Format("Departure.Probation", Names(report.ProbationPcs))));
        }

        if (report.PendingWakes.Count > 0)
        {
            lines.Children.Add(Label(Strings.Get("Common.Bullet") + Strings.Format("Departure.Wakes", Names(report.PendingWakes))));
        }

        var stay = Secondary(Strings.Get("Departure.Stay"));
        stay.Click += (_, _) => Finish(DepartureChoice.Stay);

        var leave = new Button { Content = Strings.Get("Departure.Leave"), Classes = { "danger" } };
        leave.Click += (_, _) => Finish(DepartureChoice.Leave);

        var buttons = new List<Control> { stay };
        if (report.RunningJobCount > 0)
        {
            var wait = Primary(Strings.Format("Departure.Wait", report.RunningJobCount));
            wait.Click += (_, _) => Finish(DepartureChoice.Wait);
            buttons.Add(wait);
        }

        buttons.Add(leave);
        Body(Heading(Strings.Format("Departure.Title", labName)), Label(Strings.Format("Departure.Body", labName)), lines, Buttons(buttons.ToArray()));
    }

    private static string Names(IEnumerable<int> numbers) =>
        string.Join(", ", numbers.Select(n => string.Format(Strings.Culture, Defaults.MachineNameFormat, n)));
}

/// <summary>
/// <i>Waiting for N jobs…</i>: follows the session's job book and finishes with <c>true</c>
/// once no job is delivered or running any more; <i>Cancel</i> finishes with <c>false</c>.
/// </summary>
public sealed class WaitForJobsDialog : DialogWindow<bool?>
{
    private readonly LabSession _session;
    private readonly TextBlock _text;
    private readonly Action<LabControl.Shared.Lab.JobRecord> _onUpdated;

    public WaitForJobsDialog(LabSession session)
    {
        _session = session;
        Title = Strings.Get("Departure.WaitTitle");
        _text = Label(string.Empty);

        var cancel = Secondary(Strings.Get("Common.Cancel"));
        cancel.Click += (_, _) => Finish(false);

        Body(Heading(Strings.Get("Departure.WaitTitle")), _text, new ProgressBar { IsIndeterminate = true }, Buttons(cancel));

        _onUpdated = _ => Dispatcher.UIThread.Post(Check);
        session.Jobs.Updated += _onUpdated;
        Closed += (_, _) => session.Jobs.Updated -= _onUpdated;
        Opened += (_, _) => Check();
    }

    private void Check()
    {
        var remaining = _session.DescribeDeparture().RunningJobCount;
        _text.Text = Strings.Format("Departure.Waiting", remaining);
        if (remaining == 0)
        {
            Finish(true);
        }
    }
}

/// <summary>The local paths behind a drag-and-drop, or none when what was dragged is not files.</summary>
public static class DroppedFiles
{
    public static IReadOnlyList<string> PathsOf(DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files is null)
        {
            return [];
        }

        return files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
    }
}

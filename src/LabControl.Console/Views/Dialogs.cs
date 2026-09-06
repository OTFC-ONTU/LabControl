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
/// Development-only (D-31 item 3, D-27): sends one of the console's built-in test scripts.
/// Every <c>run_script</c> parameter is selectable so each can be proved on the VM; the
/// teacher's script library replaces this in M4.
/// </summary>
public sealed class TestScriptDialog : DialogWindow<TestScriptChoice>
{
    public TestScriptDialog(int pcCount)
    {
        Title = Strings.Get("Action.RunScript");

        var kinds = new ComboBox
        {
            ItemsSource = new[]
            {
                Strings.Get("Script.Kind.HundredLines"),
                Strings.Get("Script.Kind.Hang"),
                Strings.Get("Script.Kind.WhoAmI"),
            },
            SelectedIndex = 0,
            MinWidth = 380,
        };

        var shells = new ComboBox
        {
            ItemsSource = new[] { Strings.Get("Script.Shell.PowerShell"), Strings.Get("Script.Shell.Cmd") },
            SelectedIndex = 0,
            MinWidth = 380,
        };

        var runAs = new ComboBox
        {
            ItemsSource = new[] { Strings.Get("Script.RunAs.System"), Strings.Get("Script.RunAs.User") },
            SelectedIndex = 0,
            MinWidth = 380,
        };

        var timeout = Field(Strings.Get("Script.Timeout"));
        timeout.Text = ((int)Defaults.ScriptDefaultTimeout.TotalSeconds).ToString(Strings.Culture);

        var error = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };

        var ok = Primary(Strings.Get("Script.Run"));
        ok.Click += (_, _) =>
        {
            if (!int.TryParse(timeout.Text, out var seconds) || seconds <= 0)
            {
                error.Text = Strings.Get("Script.BadTimeout");
                error.IsVisible = true;
                return;
            }

            Finish(new TestScriptChoice(
                (TestScriptKind)Math.Max(0, kinds.SelectedIndex),
                shells.SelectedIndex == 1 ? ScriptShell.Cmd : ScriptShell.PowerShell,
                runAs.SelectedIndex == 1 ? ScriptRunAs.User : ScriptRunAs.System,
                TimeSpan.FromSeconds(seconds)));
        };

        var cancel = Secondary(Strings.Get("Common.Cancel"));
        cancel.Click += (_, _) => Finish(null);

        Body(
            Heading(Strings.Format("Script.Heading", pcCount)),
            Label(Strings.Get("Script.Hint")),
            Label(Strings.Get("Script.Kind")), kinds,
            Label(Strings.Get("Script.Shell")), shells,
            Label(Strings.Get("Script.RunAs")), runAs,
            Label(Strings.Get("Script.Timeout")), timeout,
            error,
            Buttons(cancel, ok));
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LabControl.Console.Localization;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Identity;

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

public sealed class ScriptDialog : DialogWindow<ScriptAnswer>
{
    public ScriptDialog(int pcCount)
    {
        Title = Strings.Get("Action.RunScript");

        var script = Field(Strings.Get("Script.Name"));
        script.Text = "hello.ps1";
        var lines = Field(Strings.Get("Script.Lines"));
        lines.Text = "5";
        var exit = Field(Strings.Get("Script.ExitCode"));
        exit.Text = "0";
        var timeout = Field(Strings.Get("Script.Timeout"));
        timeout.Text = "120";

        var ok = Primary(Strings.Get("Script.Run"));
        ok.Click += (_, _) =>
        {
            var args = new Dictionary<string, string>
            {
                ["lines"] = lines.Text ?? "5",
                ["exit"] = exit.Text ?? "0",
            };
            var seconds = int.TryParse(timeout.Text, out var t) && t > 0 ? t : 120;
            Finish(new ScriptAnswer(script.Text ?? string.Empty, args, TimeSpan.FromSeconds(seconds)));
        };

        var cancel = Secondary(Strings.Get("Common.Cancel"));
        cancel.Click += (_, _) => Finish(null);

        Body(Heading(Strings.Format("Script.Heading", pcCount)), Label(Strings.Get("Script.Hint")), script, lines, exit, timeout, Buttons(cancel, ok));
    }
}

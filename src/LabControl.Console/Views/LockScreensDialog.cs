using Avalonia.Controls;
using Avalonia.Layout;
using LabControl.Console.Localization;
using LabControl.Console.ViewModels;
using LabControl.Shared;

namespace LabControl.Console.Views;

/// <summary>
/// The <i>Lock screens</i> dialog (M6 portion 1, D-70): the message the students see, how
/// long — until the teacher lifts it, or a number of minutes — and the two plain sentences
/// every lock needs said: the ceiling every PC enforces on its own
/// (<see cref="Defaults.LockHardLimit"/>), and that Ctrl+Alt+Del is not blocked.
/// </summary>
public sealed class LockScreensDialog : DialogWindow<LockAnswer>
{
    public LockScreensDialog(int pcCount)
    {
        Title = Strings.Get("Lock.Title");
        var ceilingMinutes = (int)Defaults.LockHardLimit.TotalMinutes;

        var message = new TextBox
        {
            PlaceholderText = Strings.Get("Lock.MessagePlaceholder"),
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinWidth = 380,
            MinHeight = 64,
            MaxLength = 500,
        };

        var untilLifted = new RadioButton { Content = Strings.Get("Lock.UntilLifted"), IsChecked = true, GroupName = "duration" };
        var forMinutes = new RadioButton { Content = Strings.Get("Lock.ForMinutes"), GroupName = "duration" };
        var minutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = ceilingMinutes,
            Value = Math.Min(10, ceilingMinutes),
            Increment = 1,
            FormatString = "0",
            Width = 110,
            IsEnabled = false,
        };
        forMinutes.IsCheckedChanged += (_, _) => minutes.IsEnabled = forMinutes.IsChecked == true;
        var duration = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        duration.Children.Add(forMinutes);
        duration.Children.Add(minutes);

        var lockButton = Primary(Strings.Get("Lock.Action"));
        var cancel = Secondary(Strings.Get("Common.Cancel"));
        lockButton.Click += (_, _) =>
        {
            TimeSpan? chosen = forMinutes.IsChecked == true && minutes.Value is { } value
                ? TimeSpan.FromMinutes((double)Math.Clamp(value, 1, ceilingMinutes))
                : null;
            Finish(new LockAnswer((message.Text ?? string.Empty).Trim(), chosen));
        };
        cancel.Click += (_, _) => Finish(null);

        Body(
            Heading(Strings.Format("Lock.Targets", pcCount)),
            Label(Strings.Get("Lock.MessageLabel")),
            message,
            untilLifted,
            duration,
            Label(Strings.Format("Lock.Ceiling", ceilingMinutes)),
            Label(Strings.Get("Lock.SecureAttention")),
            Label(Strings.Get("Lock.Offline")),
            Buttons(cancel, lockButton));
    }
}

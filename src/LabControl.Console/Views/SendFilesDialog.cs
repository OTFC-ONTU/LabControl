using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LabControl.Console.Localization;
using LabControl.Console.ViewModels;

namespace LabControl.Console.Views;

public sealed class SendFilesDialog : DialogWindow<SendFilesAnswer>
{
    public SendFilesDialog(int pcCount)
    {
        Title = Strings.Get("Action.SendFiles");
        IReadOnlyList<string> paths = [];
        var summary = Label(Strings.Get("Files.None"));
        var choose = new Button { Content = Strings.Get("Files.Choose") };
        var open = new CheckBox { Content = Strings.Get("Files.Open") };
        var send = Primary(Strings.Get("Files.Send"));
        send.IsEnabled = false;
        var cancel = Secondary(Strings.Get("Common.Cancel"));
        choose.Click += async (_, _) =>
        {
            var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("Files.Choose"), AllowMultiple = true,
            });
            if (selected.Count == 0) return;
            var local = selected.Select(f => f.TryGetLocalPath()).ToArray();
            if (local.Any(p => p is null))
            {
                paths = [];
                summary.Text = Strings.Get("Files.LocalOnly");
            }
            else
            {
                paths = local.Select(p => p!).ToArray();
                summary.Text = Strings.Format("Files.Selected", paths.Count);
            }
            send.IsEnabled = paths.Count > 0;
        };
        send.Click += (_, _) => Finish(new SendFilesAnswer(paths, open.IsChecked == true));
        cancel.Click += (_, _) => Finish(null);
        Body(Heading(Strings.Format("Files.Targets", pcCount)), Label(Strings.Get("Files.Description")),
            Label(Strings.Get("Files.Availability")), choose, summary, open, Label(Strings.Get("Files.OpenHint")), Buttons(cancel, send));
    }
}

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Jobs;

namespace LabControl.Console.Views;

/// <summary>
/// The folder on the PCs and where collected work lands on this computer — shared by
/// <i>Collect files…</i> and the exam's collect switch (M6 portion 5, D-77).
/// </summary>
internal sealed class CollectFields
{
    public TextBox Folder { get; } = new() { Text = CollectRequest.DefaultFolder, MinWidth = 300 };

    public TextBox Destination { get; } = new() { Text = WorkCollections.DefaultDestination, MinWidth = 300 };

    public Button Browse { get; } = new() { Content = Strings.Get("Collect.Browse") };

    public CollectFields(Window owner)
    {
        Browse.Click += async (_, _) =>
        {
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Get("Collect.DestinationLabel"), AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                Destination.Text = path;
            }
        };
    }

    public StackPanel DestinationRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Destination);
        row.Children.Add(Browse);
        return row;
    }

    /// <summary>The folder and the destination as typed, or the problem in words.</summary>
    public (string? Folder, string? Destination, string? Problem) Read()
    {
        var (folder, problem) = ExamDialogModel.ParseCollectFolder(Folder.Text);
        if (problem is not null)
        {
            return (null, null, problem);
        }

        var destination = (Destination.Text ?? string.Empty).Trim();
        return destination.Length == 0 || !Path.IsPathFullyQualified(destination)
            ? (null, null, Strings.Get("Collect.DestinationMissing"))
            : (folder, destination, null);
    }
}

/// <summary>
/// <i>Collect files…</i> outside an exam (M6 portion 5, owner decision 2026-10-23): one folder
/// from the selected PCs into <c>&lt;name date&gt;/PC-07/</c>; what is not online is reported missing.
/// </summary>
public sealed class CollectFilesDialog : DialogWindow<CollectFilesAnswer>
{
    public CollectFilesDialog(int pcCount)
    {
        Title = Strings.Get("Collect.Title");
        var fields = new CollectFields(this);
        var name = new TextBox { Text = Strings.Get("Collect.DefaultName"), MinWidth = 300, MaxLength = 60 };
        var problem = new TextBlock { Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, IsVisible = false };
        var start = Primary(Strings.Get("Collect.Start"));
        var cancel = Secondary(Strings.Get("Common.Cancel"));
        start.Click += (_, _) =>
        {
            var (folder, destination, failure) = fields.Read();
            var label = (name.Text ?? string.Empty).Trim();
            if (failure is null && label.Length == 0)
            {
                failure = Strings.Get("Collect.NameMissing");
            }

            if (failure is not null)
            {
                problem.Text = failure;
                problem.IsVisible = true;
                return;
            }

            Finish(new CollectFilesAnswer(label, folder!, destination!));
        };
        cancel.Click += (_, _) => Finish(null);
        Body(
            Heading(Strings.Format("Collect.Targets", pcCount)),
            Label(Strings.Get("Collect.NameLabel")),
            name,
            Label(Strings.Get("Collect.FolderLabel")),
            fields.Folder,
            Label(Strings.Get("Collect.FolderHint")),
            Label(Strings.Get("Collect.DestinationLabel")),
            fields.DestinationRow(),
            Label(Strings.Get("Collect.OfflineNote")),
            Label(Strings.Format("Collect.Caps", Defaults.CollectMaxBytes / (1024 * 1024), Defaults.CollectMaxFiles)),
            problem,
            Buttons(cancel, start));
    }
}

/// <summary>
/// <i>Reset profile…</i> (M6 portion 5, D-78): light or full, each said in plain words, with
/// what cannot be undone and when a PC refuses; the confirm button says what it does.
/// </summary>
public sealed class ResetProfileDialog : DialogWindow<ResetMode?>
{
    public ResetProfileDialog(int pcCount)
    {
        Title = Strings.Get("Reset.Title");
        var light = new RadioButton { GroupName = "reset", IsChecked = true, Content = Strings.Get("Reset.Light") };
        var full = new RadioButton { GroupName = "reset", Content = Strings.Get("Reset.Full") };
        var lightNote = Label(Strings.Get("Reset.LightNote"));
        lightNote.Margin = new Avalonia.Thickness(24, 0, 0, 0);
        var fullNote = Label(Strings.Get("Reset.FullNote"));
        fullNote.Margin = new Avalonia.Thickness(24, 0, 0, 0);
        var warning = Label(Strings.Get("Reset.Warning"));
        warning.Foreground = Brushes.DarkOrange;
        var confirm = Primary(Strings.Get("Reset.ConfirmLight"));
        confirm.Classes.Remove("accent");
        confirm.Classes.Add("danger");
        var cancel = Secondary(Strings.Get("Common.Cancel"));
        confirm.IsDefault = false;
        cancel.IsDefault = true;
        full.IsCheckedChanged += (_, _) => confirm.Content = Strings.Get(full.IsChecked == true ? "Reset.ConfirmFull" : "Reset.ConfirmLight");
        confirm.Click += (_, _) => Finish(full.IsChecked == true ? ResetMode.Full : ResetMode.Light);
        cancel.Click += (_, _) => Finish(null);
        Body(
            Heading(Strings.Format("Reset.Targets", pcCount)),
            light,
            lightNote,
            full,
            fullNote,
            warning,
            Buttons(cancel, confirm));
    }
}

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LabControl.Console.Localization;
using LabControl.Console.ViewModels;
using LabControl.Shared.Packages;

namespace LabControl.Console.Views;

/// <summary>
/// The <i>Add package</i> wizard (M7 portion 3, D-88): choose an installer, read what was
/// found and proposed — the kind of installer, its silent arguments, how a PC will know it
/// is installed — correct anything, and add it to the catalog. One page, the fields
/// disabled until an installer is chosen; the copy shows its progress and can be cancelled.
/// </summary>
public sealed class AddPackageDialog : DialogWindow<PackageEntry>
{
    private readonly AddPackageModel _model;
    private readonly TextBlock _file = Label(Strings.Get("Packages.NoInstaller"));
    private readonly TextBlock _family = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private readonly TextBlock _familyHint = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Opacity = 0.75, FontSize = 12 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsVisible = false, MinWidth = 380 };
    private readonly TextBox _name = new() { MinWidth = 320, MaxLength = PackageEntry.MaxNameLength };
    private readonly TextBox _version = new() { MinWidth = 160, MaxLength = 64 };
    private readonly TextBox _silent = new() { MinWidth = 320, MaxLength = 1024, FontFamily = new FontFamily("Menlo, Consolas, monospace") };
    private readonly RadioButton _byName = new() { GroupName = "detect", IsChecked = true };
    private readonly RadioButton _byPath = new() { GroupName = "detect" };
    private readonly TextBox _detectName = new() { MinWidth = 320, MaxLength = 260 };
    private readonly TextBox _detectPath = new() { MinWidth = 320, MaxLength = 260 };
    private readonly TextBlock _replaces = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Foreground = Brushes.DarkOrange, IsVisible = false };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Foreground = Brushes.IndianRed, IsVisible = false };
    private readonly StackPanel _fields;
    private readonly Button _choose = new() { Content = Strings.Get("Packages.Choose") };
    private readonly Button _add = Primary(Strings.Get("Packages.AddToCatalog"));
    private CancellationTokenSource? _work;

    public AddPackageDialog(AddPackageModel model)
    {
        _model = model;
        Title = Strings.Get("Packages.AddTitle");
        _byName.Content = new TextBlock { Text = Strings.Get("Packages.DetectByName"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        _byPath.Content = new TextBlock { Text = Strings.Get("Packages.DetectByPath"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        // Short lines of text stay at the left edge; a MaxWidth label alone would sit centred.
        foreach (var text in new[] { _file, _family, _familyHint, _replaces, _problem })
        {
            text.HorizontalAlignment = HorizontalAlignment.Left;
        }

        _detectPath.PlaceholderText = Strings.Get("Packages.DetectPathPlaceholder");
        _silent.PlaceholderText = Strings.Get("Packages.SilentPlaceholder");
        _byName.IsCheckedChanged += (_, _) => ShowDetect();
        _name.TextChanged += (_, _) =>
        {
            _model.Name = _name.Text ?? string.Empty;
            ShowReplaces();
        };

        var cancel = Secondary(Strings.Get("Common.Cancel"));
        _add.IsEnabled = false;
        _choose.Click += async (_, _) => await ChooseAsync();
        _add.Click += async (_, _) => await AddAsync();
        cancel.Click += (_, _) =>
        {
            _work?.Cancel();
            Finish(null);
        };
        Closed += (_, _) => _work?.Cancel();

        _fields = new StackPanel { Spacing = 8, IsEnabled = false };
        _fields.Children.Add(_family);
        _fields.Children.Add(_familyHint);
        _fields.Children.Add(Pair(Strings.Get("Packages.Name"), _name));
        _fields.Children.Add(Pair(Strings.Get("Packages.Version"), _version));
        _fields.Children.Add(Pair(Strings.Get("Packages.Silent"), _silent));
        _fields.Children.Add(new TextBlock { Text = Strings.Get("Packages.DetectHeading"), TextWrapping = TextWrapping.Wrap, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left });
        _fields.Children.Add(Indented(_byName, _detectName));
        _fields.Children.Add(Indented(_byPath, _detectPath));
        _fields.Children.Add(_replaces);
        ShowDetect();

        Body(Heading(Strings.Get("Packages.AddTitle")), Label(Strings.Get("Packages.AddDescription")),
            _choose, _file, _progress, _fields, _problem, Label(Strings.Get("Packages.SystemNote")), Buttons(cancel, _add));
    }

    private async Task ChooseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("Packages.Choose"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Strings.Get("Packages.InstallerFiles")) { Patterns = ["*.exe", "*.msi", "*.EXE", "*.MSI"] }],
        });
        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not { } path)
        {
            ShowProblem(Strings.Get("Files.LocalOnly"));
            return;
        }

        await RunAsync(async token =>
        {
            _file.Text = Strings.Get("Packages.Reading");
            var problem = await _model.InspectAsync(path, new Progress<double>(v => _progress.Value = v), token);
            _file.Text = _model.FileText;
            if (problem is not null)
            {
                ShowProblem(problem);
                _fields.IsEnabled = false;
                _add.IsEnabled = false;
                return;
            }

            _family.Text = _model.FamilyText;
            _familyHint.Text = _model.FamilyHint;
            _name.Text = _model.Name;
            _version.Text = _model.Version;
            _silent.Text = _model.Silent;
            _detectName.Text = _model.DetectName;
            _detectPath.Text = _model.DetectPath;
            _byName.IsChecked = _model.DetectKind == DetectKind.UninstallName;
            _byPath.IsChecked = _model.DetectKind == DetectKind.Path;
            ShowReplaces();
            _fields.IsEnabled = true;
            _add.IsEnabled = true;
        });
    }

    private async Task AddAsync()
    {
        _model.Name = _name.Text ?? string.Empty;
        _model.Version = _version.Text ?? string.Empty;
        _model.Silent = _silent.Text ?? string.Empty;
        _model.DetectKind = _byPath.IsChecked == true ? DetectKind.Path : DetectKind.UninstallName;
        _model.DetectName = _detectName.Text ?? string.Empty;
        _model.DetectPath = _detectPath.Text ?? string.Empty;
        if (_model.Draft().Problem is { } invalid)
        {
            ShowProblem(invalid);
            return;
        }

        PackageEntry? added = null;
        await RunAsync(async token =>
        {
            _file.Text = Strings.Get("Packages.Copying");
            var (entry, problem) = await _model.AddAsync(new Progress<double>(v => _progress.Value = v), token);
            _file.Text = _model.FileText;
            if (problem is not null)
            {
                ShowProblem(problem);
                return;
            }

            added = entry;
        });
        if (added is not null)
        {
            Finish(added);
        }
    }

    /// <summary>One piece of work at a time, with the controls held while it runs; a cancelled one says nothing.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        _work = new CancellationTokenSource();
        _problem.IsVisible = false;
        _choose.IsEnabled = false;
        _add.IsEnabled = false;
        _fields.IsEnabled = false;
        _progress.Value = 0;
        _progress.IsVisible = true;
        try
        {
            await work(_work.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _progress.IsVisible = false;
            _choose.IsEnabled = true;
            _fields.IsEnabled = _model.Info is not null;
            _add.IsEnabled = _model.Info is not null;
            _work.Dispose();
            _work = null;
        }
    }

    private void ShowDetect()
    {
        _detectName.IsEnabled = _byName.IsChecked == true;
        _detectPath.IsEnabled = _byPath.IsChecked == true;
    }

    private void ShowReplaces()
    {
        _replaces.Text = _model.ReplacesText;
        _replaces.IsVisible = _replaces.Text.Length > 0;
    }

    private void ShowProblem(string text)
    {
        _problem.Text = text;
        _problem.IsVisible = true;
    }

    private static Grid Pair(string label, Control field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        grid.Children.Add(text);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return grid;
    }

    private static StackPanel Indented(Control choice, Control field)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(choice);
        field.Margin = new Avalonia.Thickness(28, 0, 0, 0);
        panel.Children.Add(field);
        return panel;
    }
}

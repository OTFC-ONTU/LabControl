using System.Security.Cryptography;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared.Lab;
using LabControl.Shared.Packages;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The package catalog (M7 portion 3, D-88): the wizard's proposals, the copy into the
/// catalog, a new version replacing the old one, the teacher's edits, the YAML a backup
/// carries, and what an entry without its installer looks like.
/// </summary>
public sealed class PackageCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static PackageCatalog Open(LabStore store, string instance = "MacBook") =>
        new(store, instance, () => Now, TestLogging.Factory.CreateLogger<PackageCatalog>());

    private static LabStore NewStore()
    {
        var store = new LabStore(TestConsole.TempDirectory());
        store.EnsureDirectories();
        return store;
    }

    /// <summary>An installer the inspector knows as NSIS: a PE stub with NSIS's first-header marker after it.</summary>
    internal static string NsisInstaller(string name, string tail = "")
    {
        var directory = TestConsole.TempDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var stub = File.ReadAllBytes(typeof(Google.Protobuf.MessageParser).Assembly.Location);
        File.WriteAllBytes(path, [.. stub, 0, 0, 0, 0, 0xEF, 0xBE, 0xAD, 0xDE, .. "NullsoftInst"u8, .. System.Text.Encoding.UTF8.GetBytes(tail)]);
        return path;
    }

    internal static async Task<PackageEntry> AddAsync(PackageCatalog catalog, string path, string name, Action<AddPackageModel>? edit = null)
    {
        var model = new AddPackageModel(catalog);
        Assert.Null(await model.InspectAsync(path, null, TestContext.Current.CancellationToken));
        model.Name = name;
        edit?.Invoke(model);
        var (entry, problem) = await model.AddAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(problem);
        return entry!;
    }

    [Fact]
    public async Task The_wizard_proposes_from_the_installer_and_copies_it_into_the_catalog()
    {
        var store = NewStore();
        var catalog = Open(store);
        var path = NsisInstaller("ideaIC-2026.2.exe");
        var model = new AddPackageModel(catalog);
        Assert.Null(await model.InspectAsync(path, null, TestContext.Current.CancellationToken));

        Assert.Equal(InstallerFamily.Nsis, model.Info!.Family);
        Assert.Equal("/S", model.Silent);
        Assert.Equal(DetectKind.UninstallName, model.DetectKind);
        Assert.Equal(model.Name, model.DetectName);
        Assert.False(string.IsNullOrEmpty(model.Name));
        Assert.Equal(string.Empty, model.ReplacesText);

        model.Name = "IntelliJ IDEA Community";
        model.DetectName = "IntelliJ IDEA";
        var (entry, problem) = await model.AddAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(problem);
        Assert.Equal("intellij-idea-community", entry!.Id);
        Assert.Equal("MacBook", entry.ChangedBy);
        Assert.Equal(Now.ToUnixTimeSeconds(), entry.ChangedAtUnix);

        var copy = catalog.InstallerPath(entry);
        Assert.StartsWith(store.PackagesDirectory, copy, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(copy));
        Assert.Equal(InstallerState.Present, catalog.StateOf(entry));
        Assert.Equal(entry, catalog.Find("intellij-idea-community"));
        Assert.Equal(("MacBook", Now), catalog.LastChange());

        // The original may go: the catalog keeps its own copy.
        File.Delete(path);
        Assert.Equal(InstallerState.Present, catalog.List().Single().State);
    }

    [Fact]
    public async Task A_new_version_replaces_the_old_entry_keeps_its_corrections_and_removes_its_installer()
    {
        var store = NewStore();
        var catalog = Open(store);
        var old = await AddAsync(catalog, NsisInstaller("idea-2026.1.exe", "one"), "IntelliJ IDEA", m =>
        {
            m.Version = "2026.1";
            m.Silent = "/S /CONFIG=silent.config";
            m.DetectKind = DetectKind.Path;
            m.DetectPath = "%ProgramFiles%\\JetBrains\\IDEA\\bin\\idea64.exe";
        });
        var oldCopy = catalog.InstallerPath(old);

        var model = new AddPackageModel(catalog);
        Assert.Null(await model.InspectAsync(NsisInstaller("idea-2026.2.exe", "two"), null, TestContext.Current.CancellationToken));
        model.Name = "intellij idea";
        Assert.Equal(old.Id, model.Replaces()!.Id);
        Assert.Contains("IntelliJ IDEA", model.ReplacesText, StringComparison.Ordinal);

        model.Name = "IntelliJ IDEA";
        var (entry, problem) = await model.AddAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(problem);

        var only = Assert.Single(catalog.List());
        Assert.Equal(old.Id, only.Id);
        Assert.Equal(entry!.Sha256, only.Entry!.Sha256);
        Assert.NotEqual(old.Sha256, entry.Sha256);
        Assert.False(File.Exists(oldCopy));
        Assert.True(File.Exists(catalog.InstallerPath(entry)));
    }

    [Fact]
    public async Task Inspecting_an_installer_whose_name_is_in_the_catalog_proposes_the_teachers_own_arguments()
    {
        var store = NewStore();
        var catalog = Open(store);
        var first = NsisInstaller("tool.exe", "one");
        var model = new AddPackageModel(catalog);
        Assert.Null(await model.InspectAsync(first, null, TestContext.Current.CancellationToken));
        var proposed = model.Name;
        model.Silent = "/S /NCRC";
        model.DetectKind = DetectKind.Path;
        model.DetectPath = "C:\\Tool\\tool.exe";
        Assert.Null((await model.AddAsync(null, TestContext.Current.CancellationToken)).Problem);

        var again = new AddPackageModel(catalog);
        Assert.Null(await again.InspectAsync(NsisInstaller("tool-2.exe", "two"), null, TestContext.Current.CancellationToken));
        Assert.Equal(proposed, again.Name);
        Assert.Equal("/S /NCRC", again.Silent);
        Assert.Equal(DetectKind.Path, again.DetectKind);
        Assert.Equal("C:\\Tool\\tool.exe", again.DetectPath);
    }

    [Fact]
    public async Task An_unrecognised_installer_needs_silent_arguments_before_it_is_added()
    {
        var catalog = Open(NewStore());
        var directory = TestConsole.TempDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "setup.exe");
        File.WriteAllBytes(path, File.ReadAllBytes(typeof(Google.Protobuf.MessageParser).Assembly.Location));
        var model = new AddPackageModel(catalog);
        Assert.Null(await model.InspectAsync(path, null, TestContext.Current.CancellationToken));
        Assert.Equal(InstallerFamily.Unknown, model.Info!.Family);
        Assert.Equal(string.Empty, model.Silent);

        var (entry, problem) = await model.AddAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(entry);
        Assert.Equal(LabControl.Console.Localization.Strings.Get("Packages.Problem.SilentNeeded"), problem);
        Assert.Empty(catalog.List());

        model.Silent = "/silent";
        Assert.Null((await model.AddAsync(null, TestContext.Current.CancellationToken)).Problem);
        Assert.Single(catalog.List());
    }

    [Fact]
    public async Task An_installer_that_changed_after_inspection_is_refused_and_leaves_nothing_behind()
    {
        var store = NewStore();
        var catalog = Open(store);
        var path = NsisInstaller("tool.exe");
        var model = new AddPackageModel(catalog);
        Assert.Null(await model.InspectAsync(path, null, TestContext.Current.CancellationToken));
        await File.AppendAllTextAsync(path, "tampered", TestContext.Current.CancellationToken);

        var (entry, problem) = await model.AddAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(entry);
        Assert.NotNull(problem);
        Assert.Empty(catalog.List());
        Assert.Empty(Directory.EnumerateFileSystemEntries(store.PackagesDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Edits_are_validated_and_saved_and_delete_removes_the_installer()
    {
        var store = NewStore();
        var catalog = Open(store);
        var entry = await AddAsync(catalog, NsisInstaller("tool.exe"), "Tool");

        Assert.Equal(PackageProblem.DetectPathInvalid, catalog.Update(entry with { Detect = new DetectRule(DetectKind.Path, "tool.exe") }));
        Assert.Equal(PackageProblem.None, catalog.Update(entry with { Version = " 2.0 ", Silent = "/S /D=C:\\Tool" }));
        var saved = catalog.Find(entry.Id)!;
        Assert.Equal("2.0", saved.Version);
        Assert.Equal("/S /D=C:\\Tool", saved.Silent);
        Assert.Equal(entry.Sha256, saved.Sha256);

        catalog.Delete(entry.Id);
        Assert.Empty(catalog.List());
        Assert.False(Directory.Exists(Path.Combine(store.PackagesDirectory, entry.Id)));
    }

    [Fact]
    public async Task A_backup_carries_the_entries_and_another_computer_sees_their_installers_missing()
    {
        var store = NewStore();
        var catalog = Open(store);
        var entry = await AddAsync(catalog, NsisInstaller("tool.exe"), "Tool");

        var yaml = store.ReadCatalog();
        Assert.Equal(["tool.yaml"], yaml.Keys);
        Assert.Contains("uninstall_name:", yaml["tool.yaml"], StringComparison.Ordinal);

        var other = NewStore();
        other.WriteCatalog(yaml);
        var there = Open(other, "Desk PC");
        var item = Assert.Single(there.List());
        Assert.Equal(entry, item.Entry);
        Assert.Equal(InstallerState.Missing, item.State);
        Assert.Contains(LabControl.Console.Localization.Strings.Get("Packages.State.Missing"), new PackageRowViewModel(item).StateText, StringComparison.Ordinal);
    }

    [Fact]
    public void Hand_written_and_newer_files_are_read_or_refused_by_their_own_rules()
    {
        var store = NewStore();
        Directory.CreateDirectory(store.PackagesDirectory);
        var hash = Convert.ToHexStringLower(SHA256.HashData("x"u8));
        // The README's example: unknown keys ignored, no family (taken from the extension), no size.
        File.WriteAllText(Path.Combine(store.PackagesDirectory, "jdk.yaml"), $"""
            name: jdk
            version: "21"
            file: OpenJDK21.msi
            sha256: "{hash}"
            silent: /qn
            detect:
              path: "C:\\Program Files\\Eclipse Adoptium\\jdk-21\\bin\\java.exe"
            run_as: system
            reboot: never
            """);
        File.WriteAllText(Path.Combine(store.PackagesDirectory, "future.yaml"), $"schema_version: 99\nname: x\nfile: x.exe\nsha256: \"{hash}\"\ndetect:\n  path: C:\\x.exe\n");
        File.WriteAllText(Path.Combine(store.PackagesDirectory, "broken.yaml"), "name: [unclosed");

        var items = Open(store).List();
        var jdk = items.Single(i => i.Id == "jdk").Entry!;
        Assert.Equal(InstallerFamily.Msi, jdk.Family);
        Assert.Equal(DetectKind.Path, jdk.Detect.Kind);
        Assert.Equal("C:\\Program Files\\Eclipse Adoptium\\jdk-21\\bin\\java.exe", jdk.Detect.Value);
        Assert.Null(items.Single(i => i.Id == "future").Entry);
        Assert.Null(items.Single(i => i.Id == "broken").Entry);
        Assert.NotNull(items.Single(i => i.Id == "broken").Problem);
    }

    [Fact]
    public void Every_word_the_catalog_puts_together_from_a_code_exists()
    {
        var keys = Enum.GetValues<InstallerFamily>().Select(f => "Packages.Family." + f)
            .Concat(Enum.GetValues<InstallerFamily>().Select(f => "Packages.Hint." + f))
            .Concat(Enum.GetValues<InstallerState>().Select(s => "Packages.State." + s))
            .Concat(Enum.GetValues<PackageProblem>().Where(p => p != PackageProblem.None).Select(p => "Packages.Problem." + p));
        var defined = System.Xml.Linq.XDocument.Load(Path.Combine(ConsoleLanguageTests.RepoRoot(), "src", "LabControl.Console", "Localization", "Strings.resx")).Root!
            .Elements("data").Select(data => (string)data.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
        // ResourceTranslationTests holds the Ukrainian and Russian siblings to the neutral file.
        Assert.Empty(keys.Except(defined));
    }

    /// <summary>
    /// A dialog with a default body in <see cref="IDialogs"/> (it answers "cancelled") must still be
    /// forwarded by the real windows: the main window forgot <i>Add package</i> once, and the button
    /// did nothing (2026-10-27).
    /// </summary>
    [Theory]
    [InlineData(typeof(LabControl.Console.Views.MainWindow))]
    [InlineData(typeof(LabControl.Console.Views.WindowDialogs))]
    public void The_real_windows_implement_every_dialog_themselves(Type window)
    {
        var map = window.GetInterfaceMap(typeof(IDialogs));
        var missing = map.TargetMethods.Where(m => m.DeclaringType == typeof(IDialogs)).Select(m => m.Name).ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public async Task The_packages_tab_adds_through_the_wizard_edits_in_place_and_deletes_after_asking()
    {
        await using var console = await TestConsole.CreateLabAsync("MacBook");
        var catalog = console.Session.Packages;
        var dialogs = new WizardDialogs(NsisInstaller("tool.exe"), "Tool");
        var tab = new PackagesViewModel(console.Session, dialogs, action => action(), () => []);
        Assert.True(tab.IsEmpty);
        Assert.False(tab.HasEntry);

        await tab.AddCommand.ExecuteAsync(null);
        Assert.False(tab.IsEmpty);
        Assert.Equal("Tool", tab.Selected!.Name);
        Assert.True(tab.HasEntry);
        Assert.Equal("/S", tab.Silent);
        Assert.Contains("MacBook", tab.LastChanged, StringComparison.Ordinal);

        tab.DetectIndex = 1;
        tab.DetectValue = "tool.exe";
        tab.SaveCommand.Execute(null);
        Assert.Equal(LabControl.Console.Localization.Strings.Get("Packages.Problem.DetectPathInvalid"), tab.Error);
        Assert.Equal(DetectKind.UninstallName, catalog.List().Single().Entry!.Detect.Kind);

        tab.DetectValue = "C:\\Tool\\tool.exe";
        tab.Version = "2.0";
        tab.SaveCommand.Execute(null);
        Assert.Equal(string.Empty, tab.Error);
        var saved = catalog.List().Single().Entry!;
        Assert.Equal(new DetectRule(DetectKind.Path, "C:\\Tool\\tool.exe"), saved.Detect);
        Assert.Equal("2.0", tab.Version);

        dialogs.Confirm = false;
        await tab.DeleteCommand.ExecuteAsync(null);
        Assert.Single(catalog.List());
        dialogs.Confirm = true;
        await tab.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(catalog.List());
        Assert.True(tab.IsEmpty);
        tab.Detach();
    }

    /// <summary>The wizard answered by the model itself with one installer and a name; every other dialog cancelled.</summary>
    internal sealed class WizardDialogs(string installer, string name) : IDialogs
    {
        public bool Confirm { get; set; }

        public async Task<PackageEntry?> AddPackageAsync(AddPackageModel model)
        {
            Assert.Null(await model.InspectAsync(installer, null, CancellationToken.None));
            model.Name = name;
            return (await model.AddAsync(null, CancellationToken.None)).Entry;
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false) => Task.FromResult(Confirm);

        public Task<UnlockAnswer?> UnlockAsync(string reason, bool offerRemember = false) => Task.FromResult<UnlockAnswer?>(null);

        public Task<HolderAnswer?> AddHolderAsync(string? heading = null, string? hint = null) => Task.FromResult<HolderAnswer?>(null);

        public Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowRecoveryCodeAsync(LabControl.Shared.Identity.RecoveryCode code) => Task.FromResult(false);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult<string?>(null);

        public Task<string?> PickOpenFileAsync(string title, string extension) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task ShowImportResultsAsync(IReadOnlyList<ImportFileResult> results) => Task.CompletedTask;

        public Task<SendFilesAnswer?> SendFilesAsync(int pcCount) => Task.FromResult<SendFilesAnswer?>(null);

        public Task<LockAnswer?> LockScreensAsync(int pcCount) => Task.FromResult<LockAnswer?>(null);

        public Task<InternetAnswer?> InternetAsync(InternetDialogModel model) => Task.FromResult<InternetAnswer?>(null);

        public Task<ExamRequest?> ExamAsync(ExamDialogModel model) => Task.FromResult<ExamRequest?>(null);

        public Task<TimeSpan?> ExtendExamAsync(int pcCount) => Task.FromResult<TimeSpan?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<LabControl.Shared.Setup.AgentBuild?> PushAgentBuildAsync(int pcCount) => Task.FromResult<LabControl.Shared.Setup.AgentBuild?>(null);

        public void ShowScreen(ScreenViewModel screen)
        {
        }
    }
}

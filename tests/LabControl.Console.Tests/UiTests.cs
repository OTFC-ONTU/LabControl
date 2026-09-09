using Xunit;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Console.Views;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Tests;

/// <summary>Entry point the headless session builds the real <see cref="App"/> from, drawing with Skia.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The windows, built and rendered for real off screen: the wizard's steps and the main
/// window with a lab of fake PCs. Each test also writes a PNG under <c>LABCONTROL_UI_SHOTS</c>
/// when that variable names a directory, so a developer without a display can look.
/// </summary>
public sealed class UiTests
{
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

    [Fact]
    public async Task The_first_run_wizard_creates_a_lab_and_insists_on_a_backup()
    {
        var directory = TestConsole.TempDirectory();
        var backupPath = Path.Combine(TestConsole.TempDirectory(), "lab.lcbak");
        var options = new ConsoleOptions { DataDirectory = directory, Port = 0, BindAddress = System.Net.IPAddress.Loopback };
        var bootstrap = new ConsoleBootstrap(options, TestLogging.Factory);

        var session = await Session.Dispatch(async () =>
        {
            var window = new SetupWindow(bootstrap);
            window.Show();
            await Render(window, "wizard-1-choose");

            var vm = (SetupViewModel)window.DataContext!;
            vm.ChooseCreateCommand.Execute(null);
            await Render(window, "wizard-2-create-empty");

            // Validation stops an empty form.
            await vm.CreateCommand.ExecuteAsync(null);
            Assert.NotEmpty(vm.Error);

            vm.LabName = "Room 214";
            vm.HolderName = "Viacheslav";
            vm.Passphrase = "correct horse battery staple";
            vm.PassphraseAgain = "correct horse battery staple";
            vm.InstanceName = "MacBook";
            await vm.CreateCommand.ExecuteAsync(null);
            Assert.Equal(string.Empty, vm.Error);
            Assert.Equal(SetupStep.RecoveryCode, vm.Step);
            Assert.True(vm.RecoveryCodeText.Length >= 16);
            await Render(window, "wizard-3-recovery");

            // Cannot continue without acknowledging the code.
            vm.AcknowledgeRecoveryCodeCommand.Execute(null);
            Assert.Equal(SetupStep.RecoveryCode, vm.Step);
            vm.RecoveryCodeAcknowledged = true;
            vm.AcknowledgeRecoveryCodeCommand.Execute(null);
            Assert.Equal(SetupStep.Backup, vm.Step);
            await Render(window, "wizard-4-backup");

            // A stick written before the backup: its codes must reach the other console.
            vm.Session!.WritePayload(TestConsole.TempDirectory(), pcCount: 2);
            Assert.Equal(BackupStatus.Missing, bootstrap.CheckBackup(vm.Session.InstanceDocument));

            // The backup step goes through the file picker; drive the bootstrap the same way.
            Assert.True(bootstrap.TryExportBackup(vm.Session!, backupPath, out var error), error);
            Assert.True(File.Exists(backupPath));
            window.Close();
            return vm.Session!;
        }, TestContext.Current.CancellationToken);

        Assert.True(bootstrap.HasLab);
        Assert.Equal(BackupStatus.Current, bootstrap.CheckBackup(session.InstanceDocument));
        Assert.True(session.InstanceDocument.RecoveryCodeAcknowledged);
        await session.DisposeAsync();

        // A second console imports that backup as another teacher machine would.
        var other = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = TestConsole.TempDirectory(), Port = 0, BindAddress = System.Net.IPAddress.Loopback }, TestLogging.Factory);
        var backup = ConsoleBootstrap.ReadBackup(backupPath);
        Assert.Equal("Room 214", backup.LabName);
        Assert.Throws<UnauthorizedAccessException>(() => other.ImportBackup(backup, "wrong", null, "Lab PC"));
        await using var imported = other.ImportBackup(backup, "correct horse battery staple", null, "Lab PC");
        Assert.Equal(session.LabId, imported.LabId);
        Assert.NotEqual(session.Instance.InstanceId, imported.Instance.InstanceId);
        Assert.Equal(BackupStatus.Current, other.CheckBackup(imported.InstanceDocument));

        // The codes travelled with the backup but sleep until activated here (D-60).
        Assert.Equal(0, imported.Enrollment.UnusedCodeCount);
        Assert.Equal(2 + Shared.Defaults.SpareEnrollmentCodes, imported.Enrollment.DormantCodeCount);
        Assert.Equal(2 + Shared.Defaults.SpareEnrollmentCodes, imported.Enrollment.ActivateDormant());
        Assert.Equal(2 + Shared.Defaults.SpareEnrollmentCodes, imported.Enrollment.UnusedCodeCount);

        // Writing another stick on the new console makes its backup stale until re-exported.
        // (Timestamps are whole seconds; pretend the export happened a second earlier.)
        imported.InstanceDocument.BackupExportedAtUnix -= 1;
        imported.WritePayload(TestConsole.TempDirectory(), pcCount: 1, voidEarlier: false);
        Assert.Equal(BackupStatus.Stale, other.CheckBackup(imported.InstanceDocument));
    }

    [Fact]
    public async Task A_wizard_closed_early_resumes_at_the_missing_step_on_the_next_launch()
    {
        var directory = TestConsole.TempDirectory();
        var backupPath = Path.Combine(TestConsole.TempDirectory(), "lab.lcbak");
        var options = new ConsoleOptions { DataDirectory = directory, Port = 0, BindAddress = System.Net.IPAddress.Loopback };
        var bootstrap = new ConsoleBootstrap(options, TestLogging.Factory);
        const string passphrase = "correct horse battery staple";

        // Launch 1: the lab is created and the window is closed on the recovery-code step.
        var created = bootstrap.CreateLab("Room 214", "Viacheslav", passphrase, "MacBook", out var firstCode);
        await created.DisposeAsync();
        Assert.True(bootstrap.HasLab);
        Assert.True(bootstrap.SetupIsUnfinished(bootstrap.OpenExisting().Document));

        // Launch 2: the key is unlocked and the wizard reopens with a fresh code, since the
        // first one was never acknowledged. Closing it again after acknowledging still
        // leaves the backup owed.
        var opened = bootstrap.OpenExisting();
        Assert.True(opened.Vault!.TryUnlock(passphrase));
        var resumed = bootstrap.Start(opened, opened.Instance);
        // Dispatch has no Func<Task> overload: a lambda that returns nothing would bind to
        // Action and run as async void, so the wizard windows return the completion result.
        var abandoned = await Session.Dispatch(async () =>
        {
            var window = new SetupWindow(bootstrap, resumed);
            window.Show();
            var vm = (SetupViewModel)window.DataContext!;
            Assert.True(vm.IsResumed);
            Assert.Equal(SetupStep.RecoveryCode, vm.Step);
            Assert.NotEqual(firstCode.ToPrintableString(), vm.RecoveryCodeText);
            await Render(window, "wizard-resume-recovery");

            vm.RecoveryCodeAcknowledged = true;
            vm.AcknowledgeRecoveryCodeCommand.Execute(null);
            Assert.Equal(SetupStep.Backup, vm.Step);
            window.Close();
            return await window.Completion;
        }, TestContext.Current.CancellationToken);
        await resumed.DisposeAsync();
        Assert.Null(abandoned);

        opened = bootstrap.OpenExisting();
        Assert.True(opened.Document.RecoveryCodeAcknowledged);
        Assert.True(bootstrap.SetupIsUnfinished(opened.Document));
        Assert.False(opened.Vault!.TryUnlock(firstCode), "the unacknowledged recovery code must be void");

        // Launch 3: only the backup step is left; after the export the wizard is finished for good.
        Assert.True(opened.Vault!.TryUnlock(passphrase));
        var last = bootstrap.Start(opened, opened.Instance);
        var exported = await Session.Dispatch(async () =>
        {
            var window = new SetupWindow(bootstrap, last);
            window.Show();
            var vm = (SetupViewModel)window.DataContext!;
            Assert.Equal(SetupStep.Backup, vm.Step);
            await Render(window, "wizard-resume-backup");
            var ok = bootstrap.TryExportBackup(vm.Session!, backupPath, out var error);
            window.Close();
            return ok ? string.Empty : error;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, exported);
        await last.DisposeAsync();

        Assert.False(bootstrap.SetupIsUnfinished(bootstrap.OpenExisting().Document));
    }

    [Fact]
    public async Task The_main_window_shows_the_lab_with_fake_pcs_and_runs_a_job()
    {
        await using var console = await TestConsole.CreateLabAsync("MacBook");
        var codes = console.IssueCodes(8);
        var agents = new List<TestAgent>();
        try
        {
            for (var n = 1; n <= 8; n++)
            {
                agents.Add(TestAgent.Install(console, n, codes[n - 1]).Start());
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 8, TimeSpan.FromSeconds(15)));
            console.Session.Vault!.Lock();

            // PC-02 is locked with the helper up; PC-03's helper is down (M2 session state).
            agents[1].Link.PublishSessionState(new SessionState { Kind = SessionState.Types.Kind.Lock, User = "student", SessionId = 1, HelperAlive = true, Locked = true });
            agents[2].Link.PublishSessionState(new SessionState { User = "student", SessionId = 1, HelperAlive = false });
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(agents[1].AgentId)?.SessionLocked == true && console.Session.FindLinked(agents[2].AgentId)?.HelperAlive == false));

            var bootstrap = new ConsoleBootstrap(console.Session.Options, TestLogging.Factory);

            // Screens (M3): every PC but PC-03 sends a thumbnail once the console asked for one.
            Assert.True(await Wait.UntilAsync(() => agents.All(a => a.Link.VideoControl is { Active: true })));
            foreach (var agent in agents.Where(a => a.Number != 3))
            {
                Assert.True(agent.Link.TryPushVideo(FakeThumbnail(agent.Number)));
            }

            Assert.True(await Wait.UntilAsync(() => agents.Count(a => console.Session.Screens.Get(a.AgentId).Thumbnail.HasFrame) == 7));

            // The Func<Task<T>> overload: an async lambda without a value would bind to the
            // Action overload and run as async void, hiding every assertion.
            await Session.Dispatch<bool>(async () =>
            {
                var window = new MainWindow();
                var vm = new MainViewModel(console.Session, bootstrap, window, action => Dispatcher.UIThread.Post(action));
                window.DataContext = vm;
                window.Show();
                await Render(window, "main-1-lab");
                window.Width = 720;
                await Render(window, "main-lab-narrow");
                window.Width = 1100;
                Assert.False(vm.SendFilesCommand.CanExecute(null));
                var handouts = new SendFilesDialog(8);
                handouts.Show();
                await Render(handouts, "send-files-empty");
                Assert.False(handouts.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Send").IsEnabled);
                handouts.Close();


                Assert.Equal(7, vm.Machines.Count(m => m.HasPicture));
                Assert.False(vm.Machines.Single(m => m.Number == 3).HasPicture);
                Assert.All(vm.Machines.Where(m => m.HasPicture), m => Assert.False(m.IsPictureStale));

                // The single-PC window (what a double-click opens) asks for full mode; closing it goes back.
                var target = vm.Machines.Single(m => m.Number == 5);
                var pc5 = agents.Single(a => a.Number == 5);
                var screenWindow = new ScreenWindow(new ScreenViewModel(console.Session, target, action => Dispatcher.UIThread.Post(action)));
                screenWindow.Show();
                Assert.True(await Wait.UntilAsync(() => pc5.Link.VideoControl is { Mode: VideoMode.Full }));
                Assert.True(pc5.Link.TryPushVideo(FakeFull(5)));
                Assert.True(await Wait.UntilAsync(() => console.Session.Screens.Get(pc5.AgentId).Full.HasFrame));
                await Render(screenWindow, "screen-1-full");
                var screenVm = (ScreenViewModel)screenWindow.DataContext!;
                Assert.Same(console.Session.Screens.Get(pc5.AgentId).Full, screenVm.Image);
                screenWindow.Close();
                Assert.True(await Wait.UntilAsync(() => pc5.Link.VideoControl is { Mode: VideoMode.Thumbnail }));

                Assert.Equal(8, vm.Machines.Count);
                Assert.All(vm.Machines, m => Assert.Equal(TileStatus.Online, m.Status));
                Assert.Equal("student (locked)", vm.Machines.Single(m => m.Number == 2).SessionText);
                Assert.True(vm.Machines.Single(m => m.Number == 3).HelperDown);
                Assert.Equal("student", vm.Machines.Single(m => m.Number == 1).SessionText);
                Assert.False(vm.Machines.Single(m => m.Number == 1).HelperDown);
                Assert.Contains(vm.Banners, b => b.Key == "backup");
                Assert.DoesNotContain(vm.Banners, b => b.Key == "unlocked");

                // Select two PCs and reboot them from the toolbar.
                vm.Select(vm.Machines[0], toggle: false);
                vm.Select(vm.Machines[3], toggle: true);
                Assert.Equal(2, vm.SelectedCount);
                Assert.True(vm.SendFilesCommand.CanExecute(null));
                Assert.True(vm.RebootCommand.CanExecute(null));
                vm.RebootCommand.Execute(null);
                await Render(window, "main-2-selected");

                Assert.True(await Wait.UntilAsync(() => vm.Jobs.Count == 2 && vm.Jobs.All(j => j.IsFinished)));
                var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
                tabs.SelectedIndex = 2;
                Assert.False(vm.ExportBatchLogsCommand.CanExecute(null));
                vm.SelectedJob = vm.Jobs[0];
                Assert.True(vm.ExportBatchLogsCommand.CanExecute(null));
                await Render(window, "main-3-jobs");
                Assert.Contains(window.GetVisualDescendants().OfType<Button>(),
                    button => Equals(button.Content, "Export batch logs…") && button.IsEnabled);
                tabs.SelectedIndex = 3;
                await Render(window, "main-4-events");
                tabs.SelectedIndex = 4;
                await Render(window, "main-5-settings");
                Assert.All(vm.Jobs, j => Assert.Equal("Reboot", j.Kind));

                // The Scripts tab (M4 portion 1): a new script, edited, run on the selection as typed, then saved.
                tabs.SelectedIndex = 1;
                var scripts = vm.Scripts;
                Assert.Equal(2, scripts.PcCount);
                scripts.NewCommand.Execute(null);
                Assert.NotNull(scripts.Selected);
                Assert.Equal("new-script", scripts.Name);
                scripts.Name = "say-hello";
                scripts.Description = "Prints a greeting";
                scripts.Text = "Write-Output \"hello\"\nexit 0\n";
                await Render(window, "main-6-editor-ready");
                var editor = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single();
                editor.Text = "if ($true) {";
                scripts.RunCommand.Execute(null);
                Assert.NotEmpty(scripts.Error);
                Assert.Equal(2, vm.Jobs.Count);
                Assert.Equal(editor.Text, scripts.Text);
                editor.Text = "Write-Output \"hello\"\nexit 0\n";
                Assert.True(scripts.IsDirty);
                Assert.True(scripts.RunCommand.CanExecute(null));
                scripts.RunCommand.Execute(null);
                await Render(window, "main-6-scripts");
                Assert.True(await Wait.UntilAsync(() => vm.Jobs.Count == 4 && vm.Jobs.All(j => j.IsFinished)));
                Assert.Equal(2, vm.Jobs.Count(j => j.Kind == "Run script: say-hello"));
                Assert.Contains("not saved", scripts.Status, StringComparison.Ordinal);
                scripts.SaveCommand.Execute(null);
                Assert.False(scripts.IsDirty);
                Assert.Equal("say-hello", console.Session.Scripts.Scripts.Single().Name);
                tabs.SelectedIndex = 0;
                scripts.RunCommand.Execute(null);
                Assert.True(await Wait.UntilAsync(() => vm.Jobs.Count == 6 && vm.Jobs.All(j => j.IsFinished)));
                scripts.AddBuiltInsCommand.Execute(null);
                scripts.Selected = scripts.Scripts.Single(s => s.Name == "open-pycharm");
                await Render(window, "main-7-quick-scripts");
                tabs.SelectedIndex = 1;
                await Render(window, "main-8-editor-highlighted");
                scripts.Text = "if ($true) {";
                scripts.ValidateText();
                await Render(window, "main-9-editor-errors");
                tabs.SelectedIndex = 0;

                // Drag PC-01 to the cell of PC-06: they swap, and the layout persists.
                var first = vm.Machines.Single(m => m.Number == 1);
                var sixth = vm.Machines.Single(m => m.Number == 6);
                vm.MoveTile(first, sixth.Column, sixth.Row);
                Assert.Equal((5, 0), (first.Column, first.Row));
                Assert.Equal((0, 0), (sixth.Column, sixth.Row));
                Assert.Contains(console.Session.Registry.Document.Layout, t => t.Number == 1 && t.Column == 5);

                window.Close();
                return true;
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task The_chooser_lists_saved_labs_opens_one_on_request_and_never_by_itself()
    {
        var directory = TestConsole.TempDirectory();
        using var roomA = SavedLab.Save(directory, "Room 214");
        using var roomB = SavedLab.Save(directory, "Room 318");
        var profiles = new ProfileStore(directory);
        profiles.Touch(roomB.LabId, DateTimeOffset.UtcNow.AddDays(-1), pcCount: 12);

        var bootstrap = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = directory, Port = 0, BindAddress = System.Net.IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort }, TestLogging.Factory);
        await using var controller = new ActiveLabController(bootstrap, TestLogging.Factory);

        await Session.Dispatch<bool>(async () =>
        {
            var window = new LabChooserWindow();
            var vm = new LabChooserViewModel(bootstrap, controller, window, action => Dispatcher.UIThread.Post(action));
            window.DataContext = vm;
            window.Show();
            await Render(window, "chooser-1-labs");

            // Two rows; the last-used lab is highlighted and preselected, and nothing is active.
            Assert.Equal(2, vm.Labs.Count);
            Assert.True(vm.HasLabs);
            var highlighted = Assert.Single(vm.Labs, l => l.IsHighlighted);
            Assert.Equal(roomB.LabId, highlighted.LabId);
            Assert.Same(highlighted, vm.Selected);
            Assert.Equal("12 PCs", highlighted.PcCountText);
            Assert.Equal("Administrator", highlighted.AccessLabel);
            Assert.Equal("Ready", highlighted.StatusText);
            Assert.StartsWith("last opened", highlighted.LastUsedText, StringComparison.Ordinal);
            var other = Assert.Single(vm.Labs, l => l.LabId == roomA.LabId);
            Assert.Equal("never opened here", other.LastUsedText);
            Assert.Equal("no PCs yet", other.PcCountText);
            Assert.Null(controller.Active);
            Assert.Equal(ActivationState.Idle, controller.Status.State);
            Assert.True(vm.OpenCommand.CanExecute(null));
            Assert.True(vm.AddLabsCommand.CanExecute(null));
            Assert.True(vm.CreateLabCommand.CanExecute(null));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Open") && b.IsEnabled);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Add labs…") && b.IsEnabled);

            // Open: the selected lab becomes active; the row says so after the status arrives.
            vm.Selected = other;
            await vm.OpenCommand.ExecuteAsync(null);
            Assert.True(await Wait.UntilAsync(() => controller.Status.State == ActivationState.Active));
            Assert.Equal(roomA.LabId, controller.Active!.LabId);
            await Render(window, "chooser-2-open");
            Assert.False(vm.IsBusy);
            Assert.Equal(string.Empty, vm.Error);
            Assert.Equal("Active", vm.Labs.Single(l => l.LabId == roomA.LabId).StatusText);
            Assert.True(vm.Labs.Single(l => l.LabId == roomA.LabId).IsHighlighted);
            Assert.False(vm.RemoveCommand.CanExecute(null));

            // A failed activation shows the reason and leaves the list usable.
            File.WriteAllText(bootstrap.StoreFor(roomB.LabId).InstancePath, "not json");
            vm.Selected = vm.Labs.Single(l => l.LabId == roomB.LabId);
            await vm.OpenCommand.ExecuteAsync(null);
            Assert.True(await Wait.UntilAsync(() => controller.Status.State == ActivationState.Failed));
            await Render(window, "chooser-3-failed");
            Assert.StartsWith("Could not open Room 318", vm.Error, StringComparison.Ordinal);
            Assert.Null(controller.Active);
            Assert.True(vm.OpenCommand.CanExecute(null));
            Assert.True(vm.HasFailure);
            Assert.Equal(roomB.LabId, vm.FailedLabId);
            Assert.True(vm.RetryCommand.CanExecute(null));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Retry") && b.IsVisible && b.IsEnabled);

            // The import results dialog: one row per file.
            var results = new ImportResultsDialog(
            [
                new ImportFileResult("/sticks/room-214.lcbak", true, "Added \"Room 214\" with 14 PCs.", "Room 214"),
                new ImportFileResult("/sticks/room-318.lcbak", false, "The passphrase is wrong.", "Room 318"),
                new ImportFileResult("/sticks/notes.txt", false, "Not a file this console can add (.txt).", null),
            ]);
            results.Show();
            await Render(results, "import-results");
            Assert.Contains(results.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1 of 3 files added");
            Assert.Contains(results.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "room-318.lcbak");
            results.Close();

            // The departure dialog: the report in words, with Wait only when jobs run.
            var report = new DepartureReport(
                [new DepartureJobGroup(Job.Types.Kind.RunScript, 3, DepartureConsequence.ContinuesOnPc), new DepartureJobGroup(Job.Types.Kind.SelfUpdate, 1, DepartureConsequence.CannotBeAborted)],
                QueuedJobs: 2, UploadsInProgress: 1, ProbationPcs: [4, 9], PendingWakes: [12]);
            var departure = new DepartureDialog("Room 214", report);
            departure.Show();
            await Render(departure, "departure");
            var buttons = departure.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToList();
            Assert.Contains("Leave anyway", buttons);
            Assert.Contains("Stay", buttons);
            Assert.Contains("Wait for 4 job(s)", buttons);
            Assert.Contains(departure.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("PC-04, PC-09", StringComparison.Ordinal) == true);
            departure.Close();

            var quiet = new DepartureDialog("Room 214", new DepartureReport([], 0, 1, [], []));
            quiet.Show();
            await Render(quiet, "departure-quiet");
            Assert.DoesNotContain(quiet.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString()?.StartsWith("Wait", StringComparison.Ordinal) == true);
            quiet.Close();

            vm.Detach();
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static VideoFrame FakeThumbnail(int number)
    {
        using var picture = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(320, 180, LabControl.Shared.Video.JpegCodec.PixelFormat, SkiaSharp.SKAlphaType.Premul));
        picture.Erase(new SkiaSharp.SKColor((byte)(40 * number), (byte)(200 - 20 * number), 120));
        return new VideoFrame
        {
            Mode = VideoMode.Thumbnail,
            Width = 1920,
            Height = 1080,
            Keyframe = true,
            Jpeg = Google.Protobuf.ByteString.CopyFrom(LabControl.Shared.Video.JpegCodec.Encode(picture, 50)),
        };
    }

    private static VideoFrame FakeFull(int number)
    {
        using var picture = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1280, 720, LabControl.Shared.Video.JpegCodec.PixelFormat, SkiaSharp.SKAlphaType.Premul));
        picture.Erase(new SkiaSharp.SKColor((byte)(40 * number), 60, 200));
        var frame = new VideoFrame
        {
            Mode = VideoMode.Full,
            Width = 1280,
            Height = 720,
            Keyframe = true,
            Jpeg = Google.Protobuf.ByteString.CopyFrom(LabControl.Shared.Video.JpegCodec.Encode(picture, 75)),
        };
        frame.Dirty.Add(LabControl.Shared.Video.VideoGeometry.Whole(1280, 720));
        return frame;
    }

    [Fact]
    public async Task The_chooser_shows_teacher_states_and_settings_lists_every_device_with_its_delivery()
    {
        // Five saved labs: an administrator one and four teacher profiles in every authorization state.
        var directory = TestConsole.TempDirectory();
        using var admin = SavedLab.Save(directory, "Room 214");
        var profiles = new ProfileStore(directory);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        void Teacher(string name, ProfileAuthorization authorization, long expires = 0)
        {
            var id = Guid.NewGuid().ToString("d");
            profiles.Upsert(new ProfileRecord { LabId = id, LabName = name, AuthorityFingerprint = "f", Access = ProfileAccess.Teacher, Authorization = authorization, InstanceId = Guid.NewGuid().ToString("d"), InstanceName = "Laptop", AccessExpiresUnix = expires, AddedAtUnix = now, Source = ProfileSource.LabFile, PcCount = 14 });
        }

        Teacher("Room 101", ProfileAuthorization.NeedsAuthorization);
        Teacher("Room 102", ProfileAuthorization.RequestPending);
        Teacher("Room 103", ProfileAuthorization.Authorized, now + 20 * 24 * 3600);
        Teacher("Room 104", ProfileAuthorization.Authorized, now - 3600);
        Teacher("Room 105", ProfileAuthorization.Revoked);

        var bootstrap = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = directory, Port = 0, BindAddress = System.Net.IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort }, TestLogging.Factory);
        await using var controller = new ActiveLabController(bootstrap, TestLogging.Factory);

        await Session.Dispatch<bool>(async () =>
        {
            var window = new LabChooserWindow();
            var vm = new LabChooserViewModel(bootstrap, controller, window, action => Dispatcher.UIThread.Post(action));
            window.DataContext = vm;
            window.Show();
            await Render(window, "chooser-4-teacher-states");

            string Status(string name) => vm.Labs.Single(l => l.Name == name).StatusText;
            Assert.Equal("Ready", Status("Room 214"));
            Assert.Equal("Needs authorization", Status("Room 101"));
            Assert.Equal("Request pending", Status("Room 102"));
            Assert.StartsWith("Access expires", Status("Room 103"), StringComparison.Ordinal);
            Assert.Contains("renewal", Status("Room 103"), StringComparison.Ordinal);
            Assert.Equal("Expired", Status("Room 104"));
            Assert.Equal("Withdrawn", Status("Room 105"));

            // The expiry was written back to the index, not only shown.
            Assert.Equal(ProfileAuthorization.Expired, bootstrap.Profiles.Find(vm.Labs.Single(l => l.Name == "Room 104").LabId)!.Authorization);

            // Open is refused with an explanation until authorized; Authorize… offers the next step.
            vm.Selected = vm.Labs.Single(l => l.Name == "Room 101");
            Assert.False(vm.Selected.CanOpen);
            Assert.True(vm.AuthorizeCommand.CanExecute(null));
            Assert.Equal("Authorize…", vm.Selected.AuthorizeLabel);
            await vm.OpenCommand.ExecuteAsync(null);
            Assert.Contains("not authorized", vm.Error, StringComparison.Ordinal);
            Assert.Null(controller.Active);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Authorize…") && b.IsVisible);

            vm.Selected = vm.Labs.Single(l => l.Name == "Room 103");
            Assert.True(vm.Selected.CanOpen);
            Assert.Equal("Request renewal…", vm.Selected.AuthorizeLabel);
            vm.Selected = vm.Labs.Single(l => l.Name == "Room 214");
            Assert.Equal("Authorize requests…", vm.Selected.AuthorizeLabel);
            Assert.False(vm.Selected.CanRenewCertificate);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);

        // Settings: the one device panel, with a withdrawn device's delivery status.
        await using var console = await TestConsole.CreateLabAsync();
        var lab = console.Session.Vault!.Peek()!;
        var devices = new DeviceAccess(console.Bootstrap, id => console.Session);
        var files = TestConsole.TempDirectory();
        foreach (var name in new[] { "Laptop 1", "Laptop 2" })
        {
            var access = new AccessDocument { LabId = lab.LabId, State = AccessState.NeedsAuthorization, InstanceId = Guid.NewGuid().ToString("d"), InstanceName = name, Authority = lab.Document.Authority };
            var request = Shared.Lab.DeviceAuthorization.CreateRequest(access, new LabControl.Shared.Protection.FileSecretProtector(), lab.LabName, "1.0.0", DateTimeOffset.UtcNow);
            var path = Path.Combine(files, name + ".lcreq");
            File.WriteAllText(path, Shared.Lab.DeviceAuthorization.SerializeRequest(request));
            devices.ApproveRequest(path, lab);
        }

        await using var pc = TestAgent.InstallEnrolled(lab, 1, console.Port, pinHost: true).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 1));
        var withdrawn = console.Session.Registry.Document.Instances.Single(i => i.Name == "Laptop 2");
        Assert.True(console.Session.TryWithdrawDevice(withdrawn.InstanceId, "left", out _));
        Assert.True(await Wait.UntilAsync(() => console.Session.DeliveryOf(LabControl.Shared.Identity.LabCertificates.InstanceSerial(withdrawn.InstanceId)).IsComplete));

        await Session.Dispatch<bool>(async () =>
        {
            var window = new MainWindow();
            var vm = new MainViewModel(console.Session, console.Bootstrap, window, action => Dispatcher.UIThread.Post(action));
            window.DataContext = vm;
            window.Show();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            tabs.SelectedIndex = 4;
            await Render(window, "settings-teacher-devices");

            Assert.True(vm.Settings.IsAdministrator);
            Assert.Equal(3, vm.Settings.Devices.Count);
            var self = Assert.Single(vm.Settings.Devices, d => d.IsThisMachine);
            Assert.Equal("Administrator", self.AccessLabel);
            var one = vm.Settings.Devices.Single(d => d.Name == "Laptop 1");
            Assert.Equal("Teacher", one.AccessLabel);
            Assert.StartsWith("authorized", one.Authorized, StringComparison.Ordinal);
            Assert.True(one.CanWithdraw);
            var two = vm.Settings.Devices.Single(d => d.Name == "Laptop 2");
            Assert.True(two.IsRevoked);
            Assert.False(two.CanWithdraw);
            Assert.Equal("Withdrawal delivered to all 1 PCs.", two.Delivery);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Authorize requests…") && b.IsVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Export lab file…") && b.IsVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Withdraw access…"));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Teacher devices");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Other teacher machines");
            vm.Detach();
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Restored_jobs_are_visible_in_the_jobs_panel_of_the_session_that_owes_them()
    {
        // The departure report counts the rows a returning session brought back (D-57 items 3–4),
        // so the panel must show them: they exist before the view model does.
        var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = console.Port;
        var script = new ScriptRecord { Id = Guid.NewGuid().ToString("d"), Name = "long", Text = "Start-Sleep 60\n", TimeoutSeconds = 300 };
        Assert.True(console.Session.Scripts.TrySave(script, out var error), error);
        var jobs = console.Session.RunScript(["pc-a", "pc-b"], script);
        var now = DateTimeOffset.UtcNow;
        foreach (var job in jobs)
        {
            console.Session.Jobs.TakePending(job.AgentId, now);
        }

        Assert.Equal(2, console.Session.DescribeDeparture().RunningJobs.Sum(g => g.Count));
        var bootstrap = console.Bootstrap;
        await console.Session.DisposeAsync();

        await using var again = ResultOwnershipTests.Reopen(console.Directory, port);
        await again.StartAsync();
        Assert.Equal(2, again.DescribeDeparture().RunningJobs.Sum(g => g.Count));

        await Session.Dispatch<bool>(async () =>
        {
            var window = new MainWindow();
            var vm = new MainViewModel(again, bootstrap, window, action => Dispatcher.UIThread.Post(action));
            window.DataContext = vm;
            window.Show();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            tabs.SelectedIndex = 2;
            await Render(window, "main-9-restored-jobs");

            Assert.Equal(2, vm.Jobs.Count);
            Assert.All(vm.Jobs, row => Assert.False(row.IsFinished));
            vm.Detach();
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);

        await console.DisposeAsync();
    }

    private static async Task Render(Window window, string name)
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var directory = Environment.GetEnvironmentVariable("LABCONTROL_UI_SHOTS");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}

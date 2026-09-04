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
            console.Session.Vault.Lock();

            var bootstrap = new ConsoleBootstrap(console.Session.Options, TestLogging.Factory);

            // The Func<Task<T>> overload: an async lambda without a value would bind to the
            // Action overload and run as async void, hiding every assertion.
            await Session.Dispatch<bool>(async () =>
            {
                var window = new MainWindow();
                var vm = new MainViewModel(console.Session, bootstrap, window, action => Dispatcher.UIThread.Post(action));
                window.DataContext = vm;
                window.Show();
                await Render(window, "main-1-lab");

                Assert.Equal(8, vm.Machines.Count);
                Assert.All(vm.Machines, m => Assert.Equal(TileStatus.Online, m.Status));
                Assert.Contains(vm.Banners, b => b.Key == "backup");
                Assert.DoesNotContain(vm.Banners, b => b.Key == "unlocked");

                // Select two PCs and reboot them from the toolbar.
                vm.Select(vm.Machines[0], toggle: false);
                vm.Select(vm.Machines[3], toggle: true);
                Assert.Equal(2, vm.SelectedCount);
                Assert.True(vm.RebootCommand.CanExecute(null));
                vm.RebootCommand.Execute(null);
                await Render(window, "main-2-selected");

                Assert.True(await Wait.UntilAsync(() => vm.Jobs.Count == 2 && vm.Jobs.All(j => j.IsFinished)));
                var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
                tabs.SelectedIndex = 1;
                vm.SelectedJob = vm.Jobs[0];
                await Render(window, "main-3-jobs");
                tabs.SelectedIndex = 2;
                await Render(window, "main-4-events");
                tabs.SelectedIndex = 3;
                await Render(window, "main-5-settings");
                tabs.SelectedIndex = 0;
                Assert.All(vm.Jobs, j => Assert.Equal("Reboot", j.Kind));

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

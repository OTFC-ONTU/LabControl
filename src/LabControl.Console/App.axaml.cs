using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Console.Views;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LabControl.Console;

/// <summary>
/// Startup and the window flow (M5 §5): the single-lab migration, the profile index, then
/// the <see cref="ActiveLabController"/> and the chooser. The main window exists while a lab
/// is <see cref="ActivationState.Active"/> and is replaced by the chooser on
/// <see cref="ActivationState.Idle"/> or <see cref="ActivationState.Failed"/>. Nothing opens
/// a lab by itself: launch shows the chooser with the last-used lab highlighted.
/// <para>
/// Documents reach the console three ways (D-59 item 5) — the command line, a second launch
/// forwarding over <see cref="SingleInstance"/>, or a LaunchServices open on macOS — and all
/// three land in <see cref="OnFilesArrived"/>: queued until a window can show the import
/// results, then run through the same <see cref="LabImports"/> batch as <i>Add labs…</i>,
/// which never activates a lab.
/// </para>
/// </summary>
public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private ILoggerFactory? _loggers;
    private Microsoft.Extensions.Logging.ILogger? _log;
    private ConsoleLock? _lock;
    private SingleInstance? _instance;
    private readonly List<string> _pendingFiles = [];
    private bool _importing;
    private bool _firstActivation = true;
    private ConsoleBootstrap? _bootstrap;
    private ActiveLabController? _controller;
    private LabChooserWindow? _chooser;
    private MainWindow? _main;
    private MainViewModel? _mainViewModel;
    private bool _stopping;
    private bool _stopped;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Windows come and go with the active lab; only Quit (or the last window's close
            // button) ends the process.
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, e) =>
            {
                // The first request is held: the departure report is asked first (M5 portion 8
                // — quitting must not be a way past it), then the lab is released
                // asynchronously (an activation in flight is cancelled, its prompt closed) and
                // the UI thread stays free. Once everything is down, Shutdown() is called
                // again and this lets it pass.
                if (!_stopped)
                {
                    e.Cancel = true;
                    _ = QuitAsync();
                }
            };
            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Shows the unlock dialog — over <paramref name="owner"/>, or as the only window — and
    /// returns whether the vault is now open. <paramref name="cancellation"/> closes it with
    /// "no" (the console is shutting down while it waits).
    /// </summary>
    private static async Task<bool> PromptUnlockAsync(IClassicDesktopStyleApplicationLifetime desktop, Window? owner, LabKeyVault vault, string reason, CancellationToken cancellation = default)
    {
        var prompt = new UnlockDialog(reason);
        using var closeOnCancel = cancellation.Register(() => Dispatcher.UIThread.Post(prompt.Close));
        if (cancellation.IsCancellationRequested)
        {
            return false;
        }

        if (owner is not null && owner.IsVisible)
        {
            await prompt.ShowDialog(owner);
        }
        else
        {
            desktop.MainWindow = prompt;
            prompt.Show();
        }

        var answer = await prompt.Completion;
        return answer is not null && (answer.RecoveryCode is not null
            ? vault.TryUnlock(answer.RecoveryCode)
            : vault.TryUnlock(answer.Passphrase ?? string.Empty));
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var options = Program.Options;
        Directory.CreateDirectory(options.DataDirectory);

        // One process per data directory (M5 §2.1): taken before anything is read or written —
        // by Program.Main when it launched us, here when a host (the headless tests) did not.
        var lockError = string.Empty;
        _lock = Program.TakeLock() ?? ConsoleLock.TryAcquire(options.DataDirectory, out lockError);
        if (_lock is null)
        {
            var refused = new ConfirmDialog(Strings.Get("App.Title"), Strings.Format("App.AlreadyRunning", options.DataDirectory, lockError), Strings.Get("Common.Quit"), null, destructive: false);
            desktop.MainWindow = refused;
            refused.Show();
            await refused.Completion;
            desktop.Shutdown(1);
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            // gRPC logs a full Kestrel stack at Information for every link a PC drops;
            // LabSession already says which PC unlinked and why.
            .MinimumLevel.Override("Grpc", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(options.DataDirectory, Defaults.ConsoleLogFileName), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        _loggers = new SerilogLoggerFactory(Log.Logger);
        _log = _loggers.CreateLogger<App>();

        // The lock is ours, so the endpoint is ours too (D-59 item 5): a second launch on this
        // directory forwards its documents here instead of showing the refusal above. A socket
        // that cannot be opened costs only that convenience; the console still runs.
        try
        {
            _instance = SingleInstance.Listen(options.DataDirectory, _lock, action => Dispatcher.UIThread.Post(action), _loggers.CreateLogger<SingleInstance>());
            _instance.FilesArrived += OnFilesArrived;
        }
        catch (Exception ex)
        {
            // Deliberately every exception: the endpoint is a convenience, and no way of
            // failing to open it — an unusable socket directory, a path longer than
            // sun_path, a name someone else holds — may cost the console its startup.
            _log.LogWarning(ex, "The single-instance endpoint could not be opened; a second launch will be refused instead of forwarded");
        }

        // Finder/LaunchServices opens on macOS arrive as file activations, before or after
        // the chooser exists; the queue below holds them until a window can show the result.
        // AppKit also reports the process's own command-line arguments as opened files on the
        // first activation (a `.dll` under `dotnet`, or the positional file itself, which
        // FilesToOpen already holds): that first batch is filtered against the arguments so
        // nothing is imported twice; a later open of the same file is a real request.
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e is not FileActivatedEventArgs files)
                {
                    return;
                }

                IReadOnlyList<string> paths = files.Files.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
                if (_firstActivation)
                {
                    _firstActivation = false;
                    paths = ConsoleOptions.WithoutArgumentEcho(paths, Program.Arguments);
                }

                OnFilesArrived(paths);
            };
        }

        _pendingFiles.AddRange(options.FilesToOpen);

        // A slow keystore is the usual reason a switch misses its 2 s target on a real Mac:
        // name it in the log rather than leave it inside "open".
        var keystoreLog = _loggers.CreateLogger("LabControl.Shared.Protection");
        SecretProtectorTiming.SlowOperation = (protector, operation, elapsed) =>
            keystoreLog.LogWarning("Keystore {Protector}.{Operation} took {Elapsed:0} ms (threshold {Threshold:0} ms)", protector, operation, elapsed.TotalMilliseconds, SecretProtectorTiming.Threshold.TotalMilliseconds);

        try
        {
            // A pre-M5 single-lab directory is moved into labs/<lab_id>/ before anything
            // else reads it (M5 §2.3); a directory that is already in the new layout is left alone.
            var migrated = new ProfileMigration(options.DataDirectory, _loggers.CreateLogger<ProfileMigration>()).Run();
            if (migrated is not null)
            {
                _log.LogInformation("Single-lab data directory migrated; lab {LabId} now lives under {Labs}", migrated, Defaults.LabsDirectoryName);
            }

            _bootstrap = new ConsoleBootstrap(options, _loggers);
            _controller = new ActiveLabController(_bootstrap, _loggers,
                (opened, token) => opened.Vault is { } vault
                    ? Dispatcher.UIThread.InvokeAsync(() => PromptUnlockAsync(desktop, _chooser, vault, Strings.Get("Unlock.ReasonRemint"), token))
                    : Task.FromResult(false));
            _controller.SessionBuilt += session => Dispatcher.UIThread.Post(() => OnSessionBuilt(session));
            _controller.StatusChanged += status => Dispatcher.UIThread.Post(() => _ = OnStatusAsync(status));

            if (_bootstrap.Profiles.Profiles.Count == 0)
            {
                // First run on this device: the wizard, as before M5. It ends with "Open the
                // console", so the lab it made is opened straight away.
                var wizard = new SetupWindow(_bootstrap);
                desktop.MainWindow = wizard;
                wizard.Show();

                var finished = await wizard.Completion;
                if (finished is null)
                {
                    desktop.Shutdown();
                    return;
                }

                var labId = finished.LabId;
                await finished.DisposeAsync();
                ShowChooser();
                wizard.Close();
                await _controller.ActivateAsync(labId);
                return;
            }

            ShowChooser();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SchemaVersionException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "The console could not start");
            var error = new ConfirmDialog(Strings.Get("App.Title"), ex.Message, Strings.Get("Common.Quit"), null, destructive: false);
            desktop.MainWindow = error;
            error.Show();
            await error.Completion;
            desktop.Shutdown(1);
        }
    }

    // ------------------------------------------------------------------ the chooser

    private void ShowChooser()
    {
        if (_desktop is null || _bootstrap is null || _controller is null || _stopping)
        {
            return;
        }

        if (_chooser is { IsVisible: true })
        {
            _chooser.Activate();
            return;
        }

        var chooser = new LabChooserWindow();
        var viewModel = new LabChooserViewModel(_bootstrap, _controller, chooser, action => Dispatcher.UIThread.Post(action), _log);
        chooser.DataContext = viewModel;
        viewModel.QuitRequested += () => _desktop.Shutdown();
        viewModel.CreateRequested += () => CreateLabAsync(chooser, viewModel);
        // Files that arrived while the chooser was importing a batch of its own, or running
        // the create wizard, go as soon as it is free again.
        viewModel.BecameIdle += () => _ = PumpFilesAsync();
        chooser.Closed += (_, _) =>
        {
            viewModel.Detach();
            if (ReferenceEquals(_chooser, chooser))
            {
                _chooser = null;
                // The teacher closed the list (not Open, not a switch): nothing is active, so quit.
                if (_main is null && !_stopping)
                {
                    _desktop.Shutdown();
                }
            }
        };

        _chooser = chooser;
        _desktop.MainWindow = chooser;
        chooser.Show();
        _ = PumpFilesAsync();
    }

    /// <summary><i>Create a lab…</i> from the chooser: the wizard's create path into a new profile, then that lab is opened.</summary>
    private async Task CreateLabAsync(LabChooserWindow owner, LabChooserViewModel viewModel)
    {
        if (_bootstrap is null || _controller is null)
        {
            return;
        }

        var wizard = new SetupWindow(_bootstrap, startAtCreate: true);
        await wizard.ShowDialog(owner);
        var finished = await wizard.Completion;
        viewModel.Refresh();
        if (finished is null)
        {
            return;
        }

        var labId = finished.LabId;
        await finished.DisposeAsync();
        await _controller.ActivateAsync(labId);
    }

    // ------------------------------------------------------------------ the main window

    private async Task OnStatusAsync(ActivationStatus status)
    {
        if (_stopping || _controller is null)
        {
            return;
        }

        switch (status.State)
        {
            case ActivationState.Active:
                if (_controller.Active is { IsDisposed: false } session)
                {
                    if (_main is not null && _mainViewModel is { } connecting && connecting.Generation == session.Generation)
                    {
                        // The window has been up on the cached mosaic since SessionBuilt; the
                        // server is serving now, so the toolbar comes alive.
                        connecting.MarkConnected();
                    }
                    else
                    {
                        await ShowMainAsync(session);
                    }
                }

                break;

            case ActivationState.Idle:
            case ActivationState.Failed:
                CloseMain();
                ShowChooser();
                break;
        }

        // A batch that waited while the chooser was busy opening a lab goes now.
        _ = PumpFilesAsync();
    }

    // ------------------------------------------------------------------ documents to open

    /// <summary>
    /// Documents from the command line, a forwarded launch, a file activation or a drop on a
    /// window. An empty list is a bare second launch: the console comes to the front and
    /// nothing is imported. Every one of those routes ends here, so batches are imported one
    /// at a time and two unlock dialogs can never stack over the same profile store.
    /// </summary>
    private void OnFilesArrived(IReadOnlyList<string> files)
    {
        if (_stopping)
        {
            return;
        }

        if (files.Count == 0)
        {
            BringToFront();
            return;
        }

        _pendingFiles.AddRange(files);
        _ = PumpFilesAsync();
    }

    /// <summary>
    /// What a second launch with nothing to open asks for: this console, in front. The
    /// application itself is raised first — on macOS a process whose windows are all hidden
    /// stays behind the Finder however often a window is activated — and then whichever
    /// window is up. With no window at all (startup, or a lab opening) raising the
    /// application is all there is to do, and it is still worth doing.
    /// </summary>
    private void BringToFront()
    {
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            try
            {
                activatable.TryLeaveBackground();
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "The application could not be brought out of the background");
            }
        }

        if (((Window?)_main ?? _chooser ?? _desktop?.MainWindow) is not { } window)
        {
            return;
        }

        try
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _log?.LogDebug(ex, "The window could not be brought to the front");
        }
    }

    /// <summary>
    /// Imports what has queued, one batch at a time, through whichever window is up: the
    /// chooser's own import (refreshes its list) or the main window's drop path. With no
    /// window ready — startup, a lab opening, a dialog in the way — the files stay queued and
    /// the next window or status change tries again. Nothing here activates a lab.
    /// </summary>
    private async Task PumpFilesAsync()
    {
        if (_importing || _stopping || _pendingFiles.Count == 0)
        {
            return;
        }

        _importing = true;
        try
        {
            while (_pendingFiles.Count > 0 && !_stopping)
            {
                var batch = _pendingFiles.ToList();
                if (_chooser is { IsVisible: true } chooser && chooser.DataContext is LabChooserViewModel { IsBusy: false } viewModel)
                {
                    _pendingFiles.Clear();
                    await viewModel.ImportFilesAsync(batch);
                }
                else if (_main is { IsVisible: true } main && _mainViewModel is { IsDetached: false })
                {
                    _pendingFiles.Clear();
                    await ImportOnMainAsync(main, batch);
                }
                else
                {
                    return;
                }
            }
        }
        finally
        {
            _importing = false;
        }
    }

    /// <summary>
    /// The session exists with its saved roster and is about to start serving (D-57 item 1):
    /// the main window opens on the cached mosaic now — every tile offline, the toolbar
    /// disabled, "Connecting…" in the chip — instead of after the server is up. A lab whose
    /// setup is unfinished waits for <see cref="ActivationState.Active"/> and the wizard.
    /// </summary>
    private void OnSessionBuilt(LabSession session)
    {
        if (_stopping || _bootstrap is null || _controller is null || session.IsDisposed)
        {
            return;
        }

        if (_bootstrap.SetupIsUnfinished(session.InstanceDocument))
        {
            return;
        }

        ShowMainWindow(session, connecting: true);
    }

    private async Task ShowMainAsync(LabSession session)
    {
        if (_desktop is null || _bootstrap is null || _controller is null)
        {
            return;
        }

        if (_main is not null && _mainViewModel?.Generation == session.Generation)
        {
            return;
        }

        CloseMain();

        if (_bootstrap.SetupIsUnfinished(session.InstanceDocument))
        {
            // The wizard was closed before the recovery code was acknowledged or a backup
            // exported (ARCHITECTURE §3.7). Both steps need the lab key, and the main window
            // stays shut until they are done.
            if (session.Vault is { IsUnlocked: false } vault
                && !await PromptUnlockAsync(_desktop, _chooser, vault, Strings.Get("Unlock.ReasonResume")))
            {
                await _controller.DeactivateAsync(Strings.Get("App.SetupNotFinished"));
                return;
            }

            var wizard = new SetupWindow(_bootstrap, session);
            if (_chooser is { IsVisible: true } owner)
            {
                await wizard.ShowDialog(owner);
            }
            else
            {
                _desktop.MainWindow = wizard;
                wizard.Show();
            }

            var finished = await wizard.Completion;
            if (finished is null)
            {
                await _controller.DeactivateAsync(Strings.Get("App.SetupNotFinished"));
                return;
            }
        }

        if (!ReferenceEquals(_controller.Active, session) || session.IsDisposed)
        {
            // Superseded while the wizard was up; the controller's next status decides.
            return;
        }

        ShowMainWindow(session, connecting: false);
    }

    /// <summary>Builds the main window on <paramref name="session"/> and replaces the chooser with it.</summary>
    private void ShowMainWindow(LabSession session, bool connecting)
    {
        if (_desktop is null || _bootstrap is null || _controller is null)
        {
            return;
        }

        if (_main is not null && _mainViewModel?.Generation == session.Generation)
        {
            return;
        }

        CloseMain();

        var main = new MainWindow();
        var viewModel = new MainViewModel(session, _bootstrap, main, action => Dispatcher.UIThread.Post(action)) { IsConnecting = connecting };
        main.DataContext = viewModel;
        viewModel.DisconnectRequested += () => _ = DisconnectAsync(main, viewModel, session);
        // A drop on the main window goes through the same queue as a forwarded launch: one
        // import batch at a time, whatever asked for it.
        main.FilesDropped += OnFilesArrived;
        main.Closing += (_, e) =>
        {
            // The close button on the main window is Disconnect (M5 §5): back to the chooser,
            // after the departure report; only the chooser's Quit ends the process. A close
            // the app itself requested (CloseMain) has already let go of the window.
            if (ReferenceEquals(_main, main) && !_stopping)
            {
                e.Cancel = true;
                _ = DisconnectAsync(main, viewModel, session);
            }
        };
        main.Closed += (_, _) =>
        {
            if (ReferenceEquals(_main, main))
            {
                _main = null;
                _mainViewModel = null;
                viewModel.Detach();
                if (!_stopping)
                {
                    ShowChooser();
                }
            }
        };

        _main = main;
        _mainViewModel = viewModel;
        _desktop.MainWindow = main;
        main.Show();

        var chooser = _chooser;
        _chooser = null;
        chooser?.Close();
        _ = PumpFilesAsync();
    }

    /// <summary>Detaches the view model and closes the main window without ending the process.</summary>
    private void CloseMain()
    {
        var main = _main;
        var viewModel = _mainViewModel;
        _main = null;
        _mainViewModel = null;
        viewModel?.Detach();
        main?.Close();
    }

    /// <summary><i>Disconnect</i>: the departure report, then release, then the chooser.</summary>
    private async Task DisconnectAsync(MainWindow main, MainViewModel viewModel, LabSession session)
    {
        if (_controller is null || viewModel.IsDetached || session.IsDisposed)
        {
            return;
        }

        // A window still connecting has nothing running to report; leaving it is a plain release.
        if (!await Departure(main).MayLeaveAsync(session))
        {
            return;
        }

        // The main window goes first and the chooser shows "Leaving …" while the room is
        // released: nothing on screen is bound to a session that is going away.
        CloseMain();
        ShowChooser();
        await _controller.DeactivateAsync(Strings.Get("Departure.Reason"));
    }

    /// <summary>
    /// Quitting the application (M5 portion 8): the same departure question as
    /// <i>Disconnect</i>, asked over whichever window is up, and only then the shutdown.
    /// <i>Stay</i> keeps the console running with the lab still active — the shutdown request
    /// was already cancelled, so there is nothing to undo.
    /// </summary>
    private async Task QuitAsync()
    {
        // A second ⌘Q while the report is up must not open a second report.
        if (_stopping || _stopped || _quitting)
        {
            return;
        }

        _quitting = true;
        try
        {
            if (!await MayQuitAsync())
            {
                return;
            }
        }
        finally
        {
            _quitting = false;
        }

        await StopAsync();
    }

    /// <summary>The departure question for the quit path; <c>true</c> when the console may go.</summary>
    private async Task<bool> MayQuitAsync()
    {
        if (_controller is { } controller)
        {
            try
            {
                if (!await Departure((Window?)_main ?? _chooser).MayQuitAsync())
                {
                    _log?.LogInformation("Quit cancelled: the teacher chose to stay in lab '{Lab}'", controller.Active?.LabName);
                    return false;
                }
            }
            catch (Exception ex)
            {
                // The report is a courtesy; a dialog that cannot be shown may not trap the
                // teacher in an application that will not quit.
                _log?.LogWarning(ex, "The departure report could not be shown before quitting");
            }
        }

        return true;
    }

    /// <summary>The departure question, asked with the console's own dialogs over <paramref name="owner"/>.</summary>
    private DepartureFlow Departure(Window? owner) => new(_controller!, new WindowDeparturePrompt(owner, _desktop));

    /// <summary>
    /// <see cref="IDeparturePrompt"/> in windows: the report dialog and the wait, shown over
    /// the window that asked, or on their own when there is none left.
    /// </summary>
    private sealed class WindowDeparturePrompt(Window? owner, IClassicDesktopStyleApplicationLifetime? desktop) : IDeparturePrompt
    {
        public async Task<DepartureChoice> AskAsync(string labName, DepartureReport report)
        {
            var dialog = new DepartureDialog(labName, report);
            await ShowAsync(dialog);
            return await dialog.Completion ?? DepartureChoice.Stay;
        }

        public async Task<bool> WaitForJobsAsync(LabSession session)
        {
            var waiting = new WaitForJobsDialog(session);
            await ShowAsync(waiting);
            return await waiting.Completion == true;
        }

        private async Task ShowAsync(Window dialog)
        {
            if (owner is { IsVisible: true } visible)
            {
                await dialog.ShowDialog(visible);
                return;
            }

            if (desktop is not null)
            {
                desktop.MainWindow = dialog;
            }

            dialog.Show();
        }
    }

    /// <summary>Files dropped on the main window are added as saved labs; the active lab stays.</summary>
    private async Task ImportOnMainAsync(MainWindow main, IReadOnlyList<string> paths)
    {
        if (_bootstrap is null)
        {
            return;
        }

        var controller = _controller;
        var imports = new LabImports(_bootstrap, async backup =>
        {
            var exported = DateTimeOffset.FromUnixTimeSeconds(backup.ExportedAtUnix).ToLocalTime().ToString("g", Strings.Culture);
            var answer = await main.UnlockAsync(Strings.Format("Import.UnlockReason", backup.LabName, exported, backup.ExportedBy));
            return answer is null ? null : new BackupSecret(answer.Passphrase, answer.RecoveryCode);
        }, ConsoleBootstrap.DefaultInstanceName(), _log,
            labId => controller?.Active is { IsDisposed: false } active && string.Equals(active.LabId, labId, StringComparison.OrdinalIgnoreCase) ? active : null,
            async reason =>
            {
                var answer = await main.UnlockAsync(reason);
                return answer is null ? null : new BackupSecret(answer.Passphrase, answer.RecoveryCode);
            });

        var results = await imports.ImportAsync(paths);
        await main.ShowImportResultsAsync(results);
    }

    // ------------------------------------------------------------------ shutdown

    /// <summary>
    /// Releases everything without holding the UI thread: the endpoint stops answering, an
    /// activation in flight is cancelled (its prompt closed), the active lab is released in
    /// the fixed order, the log flushed, the lock dropped; then the shutdown that was held is
    /// let through. A release that takes longer than the budget is abandoned rather than hung
    /// on, and every remaining step runs even if one of them throws — a console that cannot
    /// quit is worse than one that quits untidily.
    /// <para>
    /// The endpoint goes first, before the ten-second wait on the controller. A launch that
    /// reached it during that wait used to be acknowledged, exit 0, and have its file dropped
    /// by <see cref="OnFilesArrived"/> — the teacher's document silently gone. Now it is
    /// refused, and the launcher starts its own console.
    /// </para>
    /// </summary>
    private async Task StopAsync()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        try
        {
            if (_instance is { } instance)
            {
                _instance = null;
                instance.BeginStopping();
                try
                {
                    await instance.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _log?.LogDebug(ex, "The single-instance endpoint did not close cleanly");
                }
            }

            _mainViewModel?.Detach();

            var controller = _controller;
            _controller = null;
            if (controller is not null)
            {
                try
                {
                    await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "The active lab was not released cleanly on shutdown");
                }
            }
        }
        finally
        {
            Log.CloseAndFlush();
            _loggers?.Dispose();
            _lock?.Dispose();
            _lock = null;

            _stopped = true;
            _desktop?.Shutdown();
        }
    }
}

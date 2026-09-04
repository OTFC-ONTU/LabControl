using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Console.Views;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LabControl.Console;

public partial class App : Application
{
    private LabSession? _session;
    private ILoggerFactory? _loggers;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Nothing closes the app until a main window exists; the wizard may be dismissed.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => Stop();
            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var options = Program.Options;
        var store = new LabStore(options.DataDirectory);
        store.EnsureDirectories();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(store.LogsDirectory, "console-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        _loggers = new SerilogLoggerFactory(Log.Logger);
        var log = _loggers.CreateLogger<App>();

        var bootstrap = new ConsoleBootstrap(options, _loggers);

        try
        {
            LabSession session;
            if (!bootstrap.HasLab)
            {
                var wizard = new SetupWindow(bootstrap);
                desktop.MainWindow = wizard;
                wizard.Show();

                var finished = await wizard.Completion;
                if (finished is null)
                {
                    desktop.Shutdown();
                    return;
                }

                session = finished;
            }
            else
            {
                var opened = bootstrap.OpenExisting();
                var instance = opened.Instance;

                if (opened.NeedsRemint)
                {
                    // ARCHITECTURE §3.8: the console leaf is re-minted from the lab key on its
                    // own machine; the teacher types the passphrase once.
                    var prompt = new UnlockDialog(Strings.Get("Unlock.ReasonRemint"));
                    desktop.MainWindow = prompt;
                    prompt.Show();
                    var answer = await prompt.Completion;
                    var unlocked = answer is not null && (answer.RecoveryCode is not null
                        ? opened.Vault.TryUnlock(answer.RecoveryCode)
                        : opened.Vault.TryUnlock(answer.Passphrase ?? string.Empty));

                    if (unlocked)
                    {
                        var reminted = bootstrap.Remint(opened.Vault, opened.Document);
                        instance.Dispose();
                        instance = reminted;
                        log.LogInformation("Console leaf re-minted; new serial {Serial}", instance.CertificateSerial);
                    }
                    else
                    {
                        log.LogWarning("Console leaf expires on {When} and was not re-minted", instance.ExpiresAt);
                    }
                }

                session = bootstrap.Start(opened, instance);
            }

            await session.StartAsync();
            _session = session;

            var main = new MainWindow();
            var viewModel = new MainViewModel(session, bootstrap, main, action => Dispatcher.UIThread.Post(action));
            main.DataContext = viewModel;

            var previous = desktop.MainWindow;
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
            previous?.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SchemaVersionException or UnauthorizedAccessException)
        {
            log.LogError(ex, "The console could not start");
            var error = new ConfirmDialog(Strings.Get("App.Title"), ex.Message, Strings.Get("Common.Quit"), null, destructive: false);
            desktop.MainWindow = error;
            error.Show();
            await error.Completion;
            desktop.Shutdown(1);
        }
    }

    private void Stop()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            try
            {
                session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        Log.CloseAndFlush();
        _loggers?.Dispose();
    }
}

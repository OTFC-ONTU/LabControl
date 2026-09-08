using Xunit;

using System.Diagnostics;
using System.Net;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Tests;

/// <summary>
/// M5 portion 6 (D-59 item 5): documents on the command line, the single-instance endpoint
/// and what a second launch does. The command line accepts only existing files with a known
/// extension; a forwarder reaches the running server and its files arrive as imports that
/// activate nothing; a second launch on the same data directory forwards and exits 0 — in
/// process and as a real child process; a stale socket left by a crash is replaced by the
/// lock holder; two data directories have two endpoints and never see each other.
/// </summary>
public sealed class SingleInstanceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ the command line

    [Fact]
    public void Positional_arguments_are_existing_files_with_a_known_extension()
    {
        var directory = TestConsole.TempDirectory();
        var labFile = Path.Combine(directory, "room.lclab");
        var backup = Path.Combine(directory, "room.lcbak");
        File.WriteAllText(labFile, "{}");
        File.WriteAllText(backup, "{}");

        Assert.True(ConsoleOptions.TryParse(["--data", directory, labFile, "--import-only", backup], out var options, out var error), error);
        Assert.Equal(Path.GetFullPath(directory), options.DataDirectory);
        Assert.True(options.ImportOnly);
        Assert.Equal([Path.GetFullPath(labFile), Path.GetFullPath(backup)], options.FilesToOpen);

        // Every known extension is accepted, case-insensitively, and the path comes back absolute.
        foreach (var extension in Defaults.ConsoleDocumentExtensions)
        {
            var file = Path.Combine(directory, "x" + extension.ToUpperInvariant());
            File.WriteAllText(file, "{}");
            Assert.True(ConsoleOptions.TryParse([file], out var parsed, out error), error);
            Assert.Equal(Path.GetFullPath(file), Assert.Single(parsed.FilesToOpen));
        }

        // Nothing on the command line: no files, no forwarding mode, as before.
        Assert.True(ConsoleOptions.TryParse([], out var plain, out _));
        Assert.Empty(plain.FilesToOpen);
        Assert.False(plain.ImportOnly);
    }

    [Fact]
    public void An_unknown_extension_or_a_missing_file_is_an_error()
    {
        var directory = TestConsole.TempDirectory();
        var notes = Path.Combine(directory, "notes.txt");
        File.WriteAllText(notes, "hello");

        Assert.False(ConsoleOptions.TryParse([notes], out _, out var error));
        Assert.Contains(".lclab", error);

        Assert.False(ConsoleOptions.TryParse([Path.Combine(directory, "gone.lcbak")], out _, out error));
        Assert.Contains("does not exist", error);

        // A switch still needs its value and an unknown switch is still refused.
        Assert.False(ConsoleOptions.TryParse(["--data"], out _, out error));
        Assert.Contains("needs a value", error);
        Assert.False(ConsoleOptions.TryParse(["--open", notes], out _, out error));
        Assert.Contains("unknown argument", error);
    }

    // ------------------------------------------------------------------ the endpoint

    [Fact]
    public void Two_data_directories_have_two_names_and_the_same_directory_one()
    {
        var a = TestConsole.TempDirectory();
        var b = TestConsole.TempDirectory();

        var nameA = SingleInstance.NameFor(a);
        Assert.StartsWith(Defaults.SingleInstanceNamePrefix, nameA);
        Assert.Equal(Defaults.SingleInstanceNamePrefix.Length + Defaults.SingleInstanceNameHashLength, nameA.Length);
        Assert.NotEqual(nameA, SingleInstance.NameFor(b));

        // A trailing separator or a relative spelling of the same directory is the same console.
        Assert.Equal(nameA, SingleInstance.NameFor(a + Path.DirectorySeparatorChar));
        Assert.Equal(nameA, SingleInstance.NameFor(Path.Combine(a, "..", Path.GetFileName(a))));

        if (!OperatingSystem.IsWindows())
        {
            Assert.EndsWith(Defaults.SingleInstanceSocketExtension, SingleInstance.EndpointFor(a));
            Assert.Equal(Path.TrimEndingDirectorySeparator(SingleInstance.SocketDirectory()), Path.GetDirectoryName(SingleInstance.EndpointFor(a)));
        }
    }

    [Fact]
    public async Task A_forwarded_backup_arrives_at_the_server_and_imports_without_activating_a_lab()
    {
        await using var origin = await TestConsole.CreateLabAsync("Origin");
        var backup = ExportBackup(origin);

        var directory = TestConsole.TempDirectory();
        var arrived = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = SingleInstance.Listen(directory, action => action(), TestLogging.Factory.CreateLogger<SingleInstance>());
        server.FilesArrived += files => arrived.TrySetResult(files);

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(server.Endpoint));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(server.Endpoint));
        }

        var outcome = await SingleInstance.ForwardAsync(directory, [backup], Wait, Cancel);
        Assert.True(outcome.Delivered, outcome.Error);
        Assert.Equal([backup], outcome.Accepted);
        Assert.Empty(outcome.Rejected);

        var files = await arrived.Task.WaitAsync(Wait, Cancel);
        Assert.Equal([backup], files);

        // What the app does with the files: the same batch as "Add labs…". The profile appears
        // as a saved lab; LabImports has no controller, so nothing can have been opened.
        var bootstrap = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = directory, Port = 0, BindAddress = IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort },
            TestLogging.Factory, () => new FileSecretProtector());
        var imports = new LabImports(bootstrap, _ => Task.FromResult<BackupSecret?>(new BackupSecret(TestConsole.Passphrase, null)), "Forwarded device", TestLogging.Factory.CreateLogger("imports"));
        var result = Assert.Single(await imports.ImportAsync(files));
        Assert.True(result.Ok, result.Message);

        bootstrap.Profiles.Load();
        var profile = Assert.Single(bootstrap.Profiles.Profiles);
        Assert.Equal(origin.Session.LabId, profile.LabId);
        Assert.Equal(ProfileAccess.Administrator, profile.Access);
        Assert.Equal(ProfileSource.Backup, profile.Source);
        using var free = ConsoleLock.TryAcquire(directory, out _);
        Assert.NotNull(free);
    }

    [Fact]
    public async Task The_server_validates_each_path_like_the_command_line_and_answers_what_it_rejected()
    {
        var directory = TestConsole.TempDirectory();
        var files = TestConsole.TempDirectory();
        var notes = Path.Combine(files, "notes.txt");
        File.WriteAllText(notes, "hello");
        var labFile = Path.Combine(files, "room.lclab");
        File.WriteAllText(labFile, "{}");

        var arrived = new List<IReadOnlyList<string>>();
        await using var server = SingleInstance.Listen(directory, action => action());
        server.FilesArrived += arrived.Add;

        var outcome = await SingleInstance.ForwardAsync(directory, [notes, Path.Combine(files, "gone.lcbak"), "relative.lclab", labFile], Wait, Cancel);
        Assert.True(outcome.Delivered, outcome.Error);
        Assert.Equal([labFile], outcome.Accepted);
        Assert.Equal(3, outcome.Rejected.Count);
        Assert.Contains(outcome.Rejected, r => r.Contains("notes.txt"));
        Assert.Contains(outcome.Rejected, r => r.Contains("does not exist"));
        Assert.Contains(outcome.Rejected, r => r.Contains("absolute"));

        // The event carried only what passed.
        Assert.Equal([labFile], Assert.Single(arrived));

        // A bare second launch: nothing to open, still acknowledged (the app brings its window up).
        var empty = await SingleInstance.ForwardAsync(directory, [], Wait, Cancel);
        Assert.True(empty.Delivered, empty.Error);
        Assert.Empty(empty.Accepted);
        Assert.Equal(2, arrived.Count);
        Assert.Empty(arrived[1]);
    }

    [Fact]
    public async Task A_second_launch_on_the_same_directory_forwards_instead_of_starting()
    {
        var directory = TestConsole.TempDirectory();
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        File.WriteAllText(labFile, "{}");

        // The first launch: the lock, then the endpoint.
        using var held = ConsoleLock.TryAcquire(directory, out var lockError);
        Assert.NotNull(held);
        var arrived = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = SingleInstance.Listen(directory, action => action());
        server.FilesArrived += files => arrived.TrySetResult(files);

        // The second launch, in process: the lock is refused, so it forwards and is done.
        Assert.Null(ConsoleLock.TryAcquire(directory, out lockError));
        Assert.NotEmpty(lockError);
        var outcome = await SingleInstance.ForwardAsync(directory, [labFile], Wait, Cancel);
        Assert.True(outcome.Delivered, outcome.Error);
        Assert.Equal([labFile], await arrived.Task.WaitAsync(Wait, Cancel));

        // The second launch as a real process: the console executable with --import-only
        // exits 0 after the running server acknowledged, and never shows a window.
        var console = Path.Combine(AppContext.BaseDirectory, "LabControl.Console.dll");
        Assert.True(File.Exists(console), console);
        var again = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.FilesArrived += files => again.TrySetResult(files);

        using var child = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { console, "--data", directory, "--import-only", labFile },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var stderr = child.StandardError.ReadToEndAsync(Cancel);
        await child.WaitForExitAsync(Cancel).WaitAsync(TimeSpan.FromSeconds(60), Cancel);
        Assert.Equal(0, child.ExitCode);
        Assert.Equal([labFile], await again.Task.WaitAsync(Wait, Cancel));
        Assert.Equal(string.Empty, (await stderr).Trim());

        // --import-only with nobody serving: the lock is free, so there is no console to add to; exit 1.
        await server.DisposeAsync();
        held.Dispose();
        using var alone = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { console, "--data", directory, "--import-only", labFile },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var aloneError = alone.StandardError.ReadToEndAsync(Cancel);
        await alone.WaitForExitAsync(Cancel).WaitAsync(TimeSpan.FromSeconds(60), Cancel);
        Assert.Equal(1, alone.ExitCode);
        Assert.Contains("No LabControl console is running", await aloneError);
    }

    [Fact]
    public async Task A_stale_socket_file_is_replaced_by_the_lock_holder()
    {
        if (OperatingSystem.IsWindows())
        {
            // A named pipe dies with its process; there is no file to go stale.
            return;
        }

        var directory = TestConsole.TempDirectory();
        var endpoint = SingleInstance.EndpointFor(directory);

        // A console that died without unlinking its socket (.NET unlinks on Dispose, a
        // kill -9 does not): something is at the path and nobody accepts on it.
        File.WriteAllText(endpoint, string.Empty);
        Assert.True(File.Exists(endpoint));

        // With nobody serving, a forwarder gives up within its timeout instead of hanging.
        var stopwatch = Stopwatch.StartNew();
        var nobody = await SingleInstance.ForwardAsync(directory, [], TimeSpan.FromSeconds(1), Cancel);
        Assert.False(nobody.Delivered);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), stopwatch.Elapsed.ToString());

        // The next console holds the lock, so the file is stale by definition: it is replaced and served.
        using var held = ConsoleLock.TryAcquire(directory, out _);
        Assert.NotNull(held);
        var arrived = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var server = SingleInstance.Listen(directory, action => action()))
        {
            server.FilesArrived += files => arrived.TrySetResult(files);
            var outcome = await SingleInstance.ForwardAsync(directory, [], Wait, Cancel);
            Assert.True(outcome.Delivered, outcome.Error);
            Assert.Empty(await arrived.Task.WaitAsync(Wait, Cancel));
        }

        // A clean stop takes the file with it.
        Assert.False(File.Exists(endpoint));
    }

    [Fact]
    public async Task Two_data_directories_never_see_each_other()
    {
        var a = TestConsole.TempDirectory();
        var b = TestConsole.TempDirectory();
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        File.WriteAllText(labFile, "{}");

        var arrivedAtA = new List<IReadOnlyList<string>>();
        var arrivedAtB = new List<IReadOnlyList<string>>();
        await using var serverA = SingleInstance.Listen(a, action => action());
        await using var serverB = SingleInstance.Listen(b, action => action());
        serverA.FilesArrived += arrivedAtA.Add;
        serverB.FilesArrived += arrivedAtB.Add;
        Assert.NotEqual(serverA.Endpoint, serverB.Endpoint);

        var toB = await SingleInstance.ForwardAsync(b, [labFile], Wait, Cancel);
        Assert.True(toB.Delivered, toB.Error);
        Assert.Equal([labFile], Assert.Single(arrivedAtB));
        Assert.Empty(arrivedAtA);

        // A third directory with no console: nothing is delivered anywhere.
        var c = TestConsole.TempDirectory();
        var toC = await SingleInstance.ForwardAsync(c, [labFile], TimeSpan.FromSeconds(1), Cancel);
        Assert.False(toC.Delivered);
        Assert.Single(arrivedAtB);
        Assert.Empty(arrivedAtA);
    }

    private static string ExportBackup(TestConsole console)
    {
        var path = Path.Combine(TestConsole.TempDirectory(), "room.lcbak");
        Assert.True(console.Bootstrap.TryExportBackup(console.Session, path, out var error), error);
        return path;
    }
}

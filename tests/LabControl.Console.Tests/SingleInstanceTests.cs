using Xunit;

using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Tests;

/// <summary>
/// The endpoint drills read and write <c>$XDG_RUNTIME_DIR</c> to put a hostile socket
/// directory where the console looks; that variable belongs to the whole process, so this
/// class runs alone rather than beside a neighbour it could confuse.
/// </summary>
[CollectionDefinition(nameof(SingleInstanceCollection), DisableParallelization = true)]
public sealed class SingleInstanceCollection;

/// <summary>
/// M5 portion 6 (D-59 item 5): documents on the command line, the single-instance endpoint
/// and what a second launch does. The command line accepts only existing ordinary files with
/// a known extension and a plausible size; a forwarder reaches the running server and its
/// files arrive as imports that activate nothing; a second launch on the same data directory
/// forwards and exits 0 — in process and as a real child process; a stale socket left by a
/// crash is replaced by the lock holder; two data directories have two endpoints and never
/// see each other. The endpoint also has to survive what is not a console on the other end:
/// hostile lines, a client that says nothing, many launches at once — and it must refuse
/// rather than swallow a batch once the console is shutting down.
/// </summary>
[Collection(nameof(SingleInstanceCollection))]
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

    /// <summary>
    /// What <see cref="File.Exists"/> alone would have let through: a directory, an empty file,
    /// a document larger than any real one, and — the one that used to hang the console for
    /// good — a named pipe, directly or behind a symbolic link.
    /// </summary>
    [Fact]
    public async Task Only_an_ordinary_file_of_a_plausible_size_is_a_document()
    {
        var directory = TestConsole.TempDirectory();

        var folder = Path.Combine(directory, "room.lclab");
        Directory.CreateDirectory(folder);
        Assert.False(ConsoleOptions.TryValidateFile(folder, out _, out var error));
        Assert.Contains("does not exist", error);

        var empty = Path.Combine(directory, "empty.lcbak");
        File.WriteAllText(empty, string.Empty);
        Assert.False(ConsoleOptions.TryValidateFile(empty, out _, out error));
        Assert.Contains("empty", error);

        // A sparse file: the length is what the rule reads, and no disk is spent proving it.
        var huge = Path.Combine(directory, "huge.lcbak");
        await using (var stream = new FileStream(huge, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(Defaults.ConsoleDocumentMaxBytes + 1);
        }

        Assert.False(ConsoleOptions.TryValidateFile(huge, out _, out error));
        Assert.Contains("bytes", error);

        var ordinary = Path.Combine(directory, "ordinary.lcbak");
        File.WriteAllText(ordinary, "{}");
        Assert.True(ConsoleOptions.TryValidateFile(ordinary, out var full, out error), error);
        Assert.Equal(ordinary, full);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A FIFO exists, is not a directory, and blocks for ever when opened: the size rule is
        // what keeps a console that is handed one alive.
        var fifo = Path.Combine(directory, "pipe.lcbak");
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo }, UseShellExecute = false })!)
        {
            await mkfifo.WaitForExitAsync(Cancel);
            Assert.Equal(0, mkfifo.ExitCode);
        }

        Assert.True(File.Exists(fifo));
        Assert.False(ConsoleOptions.TryValidateFile(fifo, out _, out error));
        Assert.Contains("ordinary file", error);

        var link = Path.Combine(directory, "link.lcbak");
        File.CreateSymbolicLink(link, fifo);
        Assert.False(ConsoleOptions.TryValidateFile(link, out _, out error));
        Assert.Contains("ordinary file", error);

        // A symbolic link to a real document is still a document.
        var good = Path.Combine(directory, "good-link.lcbak");
        File.CreateSymbolicLink(good, ordinary);
        Assert.True(ConsoleOptions.TryValidateFile(good, out full, out error), error);
        Assert.Equal(good, full);
    }

    /// <summary>
    /// The first macOS activation echoes the process's own arguments back as opened files
    /// (D-59 item 5); those are dropped, case-insensitively, and nothing else is.
    /// </summary>
    [Fact]
    public void The_first_activation_drops_what_the_command_line_already_carried()
    {
        var directory = TestConsole.TempDirectory();
        var labFile = Path.Combine(directory, "Room.lclab");
        var other = Path.Combine(directory, "other.lcbak");
        File.WriteAllText(labFile, "{}");
        File.WriteAllText(other, "{}");

        var arguments = new[] { "/usr/bin/dotnet", "LabControl.Console.dll", "--data", directory, labFile.ToUpperInvariant() };
        var kept = ConsoleOptions.WithoutArgumentEcho([labFile, other], arguments);
        Assert.Equal([other], kept);

        // Nothing echoed: everything is kept, and a path that is not a path is not a crash.
        Assert.Equal([labFile, other], ConsoleOptions.WithoutArgumentEcho([labFile, other], ["--port", "47800"]));
        Assert.Equal([labFile], ConsoleOptions.WithoutArgumentEcho([labFile], ["\0not a path"]));
        Assert.Empty(ConsoleOptions.WithoutArgumentEcho([], arguments));
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
            var endpoint = SingleInstance.EndpointFor(a);
            Assert.EndsWith(Defaults.SingleInstanceSocketExtension, endpoint);
            Assert.StartsWith(Defaults.SingleInstanceSocketPrefix, Path.GetFileName(endpoint));
            Assert.Equal(Path.TrimEndingDirectorySeparator(SingleInstance.SocketDirectory()), Path.GetDirectoryName(endpoint));
            Assert.NotEqual(endpoint, SingleInstance.EndpointFor(b));

            // sun_path holds 104 bytes on macOS and 108 on Linux, terminator included: the
            // endpoint of a default install must fit with room to spare, not by luck.
            Assert.True(Encoding.UTF8.GetByteCount(endpoint) <= 103, endpoint);
        }
    }

    /// <summary>
    /// The socket does not sit in a world-writable directory under a fully predictable name:
    /// it lives in this user's own 0700 directory, so nobody else can pre-create the path,
    /// read the teacher's document names, or make the lock holder's unlink fail on the sticky
    /// bit. A directory that cannot be proved private — a symbolic link somebody planted at
    /// the path — is refused outright; a loose directory this user does own is tightened,
    /// because only an owner may chmod it in the first place.
    /// </summary>
    [Fact]
    public async Task The_socket_lives_in_this_users_private_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            // A named pipe has no file and no directory; CurrentUserOnly is the whole rule.
            return;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var directory = TestConsole.TempDirectory();
        await using (var server = TestServer.Start(directory))
        {
            var socketDirectory = Path.GetDirectoryName(server.Endpoint)!;
            Assert.Equal(Private, File.GetUnixFileMode(socketDirectory));
            Assert.Null(new DirectoryInfo(socketDirectory).LinkTarget);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(server.Endpoint));
        }

        var previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            // A symbolic link where the socket directory belongs: nothing is bound, nothing is
            // connected to, and the console keeps running without an endpoint.
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", ShortDirectory());
            Directory.CreateSymbolicLink(SingleInstance.SocketDirectory(), ShortDirectory());
            var planted = TestConsole.TempDirectory();
            using (var held = ConsoleLock.TryAcquire(planted, out _))
            {
                Assert.NotNull(held);
                var refused = Assert.Throws<EndpointUnusableException>(() => SingleInstance.Listen(planted, held, action => action()));
                Assert.Contains("symbolic link", refused.Message);
            }

            var nothing = await SingleInstance.ForwardAsync(planted, [], TimeSpan.FromSeconds(1), Cancel);
            Assert.False(nothing.Delivered);

            // A directory left behind with a loose mode is this user's own — nobody else could
            // have chmod'ed it — so it is tightened and used rather than abandoned.
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", ShortDirectory());
            var ours = SingleInstance.SocketDirectory();
            Directory.CreateDirectory(ours);
            File.SetUnixFileMode(ours, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            var second = TestConsole.TempDirectory();
            await using var server = TestServer.Start(second);
            Assert.Equal(Private, File.GetUnixFileMode(ours));
            Assert.Equal(ours, Path.GetDirectoryName(server.Endpoint));

            var outcome = await SingleInstance.ForwardAsync(second, [], Wait, Cancel);
            Assert.True(outcome.Delivered, outcome.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
        }
    }

    [Fact]
    public async Task A_forwarded_backup_arrives_at_the_server_and_imports_without_activating_a_lab()
    {
        await using var origin = await TestConsole.CreateLabAsync("Origin");
        var backup = ExportBackup(origin);

        var directory = TestConsole.TempDirectory();
        await using var server = TestServer.Start(directory, TestLogging.Factory.CreateLogger<SingleInstance>());

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(server.Endpoint));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(server.Endpoint));
        }

        var outcome = await SingleInstance.ForwardAsync(directory, [backup], Wait, Cancel);
        Assert.True(outcome.Delivered, outcome.Error);
        Assert.Equal([backup], outcome.Accepted);
        Assert.Empty(outcome.Rejected);

        var files = await server.First.Task.WaitAsync(Wait, Cancel);
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

        // Nothing is running on that directory once this console stops: no session was opened.
        await server.DisposeAsync();
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

        await using var server = TestServer.Start(directory);

        var outcome = await SingleInstance.ForwardAsync(directory, [notes, Path.Combine(files, "gone.lcbak"), "relative.lclab", labFile, labFile], Wait, Cancel);
        Assert.True(outcome.Delivered, outcome.Error);
        Assert.Equal([labFile], outcome.Accepted);
        Assert.Equal(3, outcome.Rejected.Count);
        Assert.Contains(outcome.Rejected, r => r.Contains("notes.txt"));
        Assert.Contains(outcome.Rejected, r => r.Contains("does not exist"));
        Assert.Contains(outcome.Rejected, r => r.Contains("absolute"));

        // The event carried only what passed, and the file named twice arrived once.
        Assert.Equal([labFile], Assert.Single(server.Arrived));

        // A bare second launch: nothing to open, still acknowledged (the app brings its window up).
        var empty = await SingleInstance.ForwardAsync(directory, [], Wait, Cancel);
        Assert.True(empty.Delivered, empty.Error);
        Assert.Empty(empty.Accepted);
        Assert.Equal(2, server.Arrived.Count);
        Assert.Empty(server.Arrived[1]);

        // More files than any teacher selects: refused as a batch, on both sides of the wire.
        var many = Enumerable.Repeat(labFile, Defaults.SingleInstanceMaxOpenPaths + 1).ToList();
        var refusedHere = await SingleInstance.ForwardAsync(directory, many, Wait, Cancel);
        Assert.False(refusedHere.Delivered);
        Assert.Contains($"at most {Defaults.SingleInstanceMaxOpenPaths}", refusedHere.Error);
        Assert.Equal(2, server.Arrived.Count);
    }

    /// <summary>
    /// What arrives on the socket is not necessarily a console: an over-long line, a line that
    /// is not JSON, bytes that are not UTF-8, nulls inside the list, hundreds and thousands of
    /// paths. None of it may crash the console, hang it, or import anything — and the next
    /// real launch must still be served.
    /// </summary>
    [Fact]
    public async Task Hostile_lines_are_refused_and_leave_the_console_serving()
    {
        var directory = TestConsole.TempDirectory();
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        File.WriteAllText(labFile, "{}");
        await using var server = TestServer.Start(directory);

        // Longer than the line cap: the connection is dropped without an answer.
        var overLong = new byte[70 * 1024];
        Array.Fill(overLong, (byte)'a');
        Assert.Null(await AskRawAsync(directory, [.. overLong, (byte)'\n'], Cancel));

        // Not JSON at all, and bytes that are not UTF-8 either.
        var notJson = await AskRawAsync(directory, Encoding.UTF8.GetBytes("hello, console\n"), Cancel);
        Assert.Contains("not a request", notJson);
        var notUtf8 = await AskRawAsync(directory, [0xFF, 0xFE, 0xFF, 0xFE, (byte)'\n'], Cancel);
        Assert.Contains("not a request", notUtf8);

        // A newer schema is named rather than guessed at.
        var newer = await AskRawAsync(directory, Encoding.UTF8.GetBytes("{\"schema_version\":99,\"open\":[]}\n"), Cancel);
        Assert.Contains("schema_version 99", newer);

        // Nulls and blanks inside the list: rejected one by one, the real file still lands.
        var withNulls = await AskRawAsync(directory,
            Encoding.UTF8.GetBytes("{\"schema_version\":1,\"open\":[null,\"\",\"   \"," + JsonQuoted(labFile) + "]}\n"), Cancel);
        Assert.NotNull(withNulls);
        Assert.Contains("empty file name", withNulls);
        Assert.Contains(Path.GetFileName(labFile), withNulls);
        Assert.Equal([labFile], Assert.Single(server.Arrived));

        // Hundreds of paths: refused as one batch. Thousands: past the line cap, so the
        // connection is dropped before anything is parsed.
        var hundreds = string.Join(",", Enumerable.Repeat(JsonQuoted(labFile), 200));
        var refused = await AskRawAsync(directory, Encoding.UTF8.GetBytes("{\"schema_version\":1,\"open\":[" + hundreds + "]}\n"), Cancel);
        Assert.Contains($"at most {Defaults.SingleInstanceMaxOpenPaths}", refused);
        var thousands = string.Join(",", Enumerable.Repeat(JsonQuoted(labFile), 5000));
        Assert.Null(await AskRawAsync(directory, Encoding.UTF8.GetBytes("{\"schema_version\":1,\"open\":[" + thousands + "]}\n"), Cancel));

        // After all of that the endpoint is still the console's: a real launch is served.
        var outcome = await SingleInstance.ForwardAsync(directory, [labFile], Wait, Cancel);
        Assert.True(outcome.Delivered, outcome.Error);
        Assert.Equal(2, server.Arrived.Count);
    }

    /// <summary>A client that connects and says nothing holds nobody up: the next launch is served straight away.</summary>
    [Fact]
    public async Task A_silent_client_does_not_block_the_accept_loop()
    {
        var directory = TestConsole.TempDirectory();
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        File.WriteAllText(labFile, "{}");
        await using var server = TestServer.Start(directory);

        await using var silent = await ConnectRawAsync(directory, Cancel);
        var stopwatch = Stopwatch.StartNew();
        var outcome = await SingleInstance.ForwardAsync(directory, [labFile], Wait, Cancel);
        Assert.True(outcome.Delivered, outcome.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), stopwatch.Elapsed.ToString());
        Assert.Equal([labFile], Assert.Single(server.Arrived));
    }

    /// <summary>Several launches at once — a Finder selection opened as separate processes — are all answered.</summary>
    [Fact]
    public async Task Concurrent_launches_are_all_answered()
    {
        var directory = TestConsole.TempDirectory();
        var files = TestConsole.TempDirectory();
        var gate = new Lock();
        var arrived = new List<string>();
        await using var server = TestServer.Start(directory);
        server.Instance.FilesArrived += batch =>
        {
            lock (gate)
            {
                arrived.AddRange(batch);
            }
        };

        var paths = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var path = Path.Combine(files, $"room-{i}.lclab");
            File.WriteAllText(path, "{}");
            paths.Add(path);
        }

        var outcomes = await Task.WhenAll(paths.Select(path => SingleInstance.ForwardAsync(directory, [path], Wait, Cancel)));
        Assert.All(outcomes, outcome => Assert.True(outcome.Delivered, outcome.Error));
        lock (gate)
        {
            Assert.Equal([.. paths.Order()], [.. arrived.Order()]);
        }
    }

    /// <summary>
    /// The window the console used to lose a file in: it is shutting down, the endpoint still
    /// answered "kept it", the launcher exited 0 and the batch was dropped on the way to the
    /// UI. Now the answer is a refusal, so the launcher starts its own console instead.
    /// </summary>
    [Fact]
    public async Task A_forward_while_the_console_is_shutting_down_is_refused_rather_than_dropped()
    {
        var directory = TestConsole.TempDirectory();
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        File.WriteAllText(labFile, "{}");

        await using var server = TestServer.Start(directory);
        server.Instance.BeginStopping();

        var outcome = await SingleInstance.ForwardAsync(directory, [labFile], TimeSpan.FromSeconds(2), Cancel);
        Assert.False(outcome.Delivered);
        Assert.Contains("shutting down", outcome.Error);
        Assert.Empty(outcome.Accepted);
        Assert.Empty(server.Arrived);

        // And once the endpoint is really gone, nothing is delivered either.
        await server.DisposeAsync();
        var afterwards = await SingleInstance.ForwardAsync(directory, [labFile], TimeSpan.FromSeconds(1), Cancel);
        Assert.False(afterwards.Delivered);
        Assert.Empty(server.Arrived);
    }

    [Fact]
    public async Task A_second_launch_on_the_same_directory_forwards_instead_of_starting()
    {
        var directory = TestConsole.TempDirectory();
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        File.WriteAllText(labFile, "{}");

        // The first launch: the lock, then the endpoint.
        await using var server = TestServer.Start(directory);

        // The second launch, in process: the lock is refused, so it forwards and is done.
        Assert.Null(ConsoleLock.TryAcquire(directory, out var lockError));
        Assert.NotEmpty(lockError);
        var outcome = await SingleInstance.ForwardAsync(directory, [labFile], Wait, Cancel);
        Assert.True(outcome.Delivered, outcome.Error);
        Assert.Equal([labFile], await server.First.Task.WaitAsync(Wait, Cancel));

        // The second launch as a real process: the console executable with --import-only
        // exits 0 after the running server acknowledged, and never shows a window.
        var console = Path.Combine(AppContext.BaseDirectory, "LabControl.Console.dll");
        Assert.True(File.Exists(console), console);
        var again = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Instance.FilesArrived += batch => again.TrySetResult(batch);

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

        // A console that died without unlinking its socket (a kill -9 does not unlink): a real
        // AF_UNIX node is at the path, and nobody accepts on it. .NET removes the path its own
        // Socket bound, so the node is bound beside the endpoint and renamed onto it — what is
        // left behind is a socket inode, not a regular file standing in for one.
        var socketDirectory = Path.GetDirectoryName(endpoint)!;
        Directory.CreateDirectory(socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var abandoned = Path.Combine(socketDirectory, "abandoned.sock");
        using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            stale.Bind(new UnixDomainSocketEndPoint(abandoned));
            File.Move(abandoned, endpoint);
        }

        Assert.True(File.Exists(endpoint));
        Assert.False(File.Exists(abandoned));

        // With nobody serving, a forwarder gives up within its timeout instead of hanging.
        var stopwatch = Stopwatch.StartNew();
        var nobody = await SingleInstance.ForwardAsync(directory, [], TimeSpan.FromSeconds(1), Cancel);
        Assert.False(nobody.Delivered);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), stopwatch.Elapsed.ToString());

        // The next console holds the lock, so the node is stale by definition: it is replaced and served.
        await using (var server = TestServer.Start(directory))
        {
            var outcome = await SingleInstance.ForwardAsync(directory, [], Wait, Cancel);
            Assert.True(outcome.Delivered, outcome.Error);
            Assert.Empty(await server.First.Task.WaitAsync(Wait, Cancel));
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

        await using var serverA = TestServer.Start(a);
        await using var serverB = TestServer.Start(b);
        Assert.NotEqual(serverA.Endpoint, serverB.Endpoint);

        var toB = await SingleInstance.ForwardAsync(b, [labFile], Wait, Cancel);
        Assert.True(toB.Delivered, toB.Error);
        Assert.Equal([labFile], Assert.Single(serverB.Arrived));
        Assert.Empty(serverA.Arrived);

        // A third directory with no console: nothing is delivered anywhere.
        var c = TestConsole.TempDirectory();
        var toC = await SingleInstance.ForwardAsync(c, [labFile], TimeSpan.FromSeconds(1), Cancel);
        Assert.False(toC.Delivered);
        Assert.Single(serverB.Arrived);
        Assert.Empty(serverA.Arrived);
    }

    // ------------------------------------------------------------------ one import at a time

    /// <summary>
    /// A forwarded batch, a drop and <i>Add labs…</i> all import through the chooser, and an
    /// import asks the teacher for passphrases: two batches at once would stack two unlock
    /// dialogs over one profile store. The second batch waits for the first, and the chooser
    /// says it is busy while it does.
    /// </summary>
    [Fact]
    public async Task Import_batches_are_run_one_at_a_time()
    {
        var directory = TestConsole.TempDirectory();
        var bootstrap = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = directory, Port = 0, BindAddress = IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort },
            TestLogging.Factory, () => new FileSecretProtector());
        await using var controller = new ActiveLabController(bootstrap, TestLogging.Factory);
        var dialogs = new GatedDialogs();
        var chooser = new LabChooserViewModel(bootstrap, controller, dialogs, action => action(), TestLogging.Factory.CreateLogger("chooser"));

        Assert.False(chooser.IsBusy);
        Assert.True(chooser.AddLabsCommand.CanExecute(null));

        var first = chooser.ImportFilesAsync([Path.Combine(directory, "one.txt")]);
        Assert.True(await UntilAsync(() => dialogs.Batches == 1));
        Assert.True(chooser.IsBusy);
        Assert.False(chooser.AddLabsCommand.CanExecute(null));
        Assert.False(chooser.CreateLabCommand.CanExecute(null));

        // The second batch cannot start while the first is still showing its results.
        var second = chooser.ImportFilesAsync([Path.Combine(directory, "two.txt")]);
        await Task.Delay(200, Cancel);
        Assert.Equal(1, dialogs.Batches);
        Assert.False(second.IsCompleted);

        dialogs.LetOneThrough();
        Assert.True(await UntilAsync(() => dialogs.Batches == 2));
        Assert.False(second.IsCompleted);
        dialogs.LetOneThrough();

        var results = await first.WaitAsync(Wait, Cancel);
        Assert.False(Assert.Single(results).Ok);
        await second.WaitAsync(Wait, Cancel);
        Assert.False(chooser.IsBusy);
        Assert.True(chooser.AddLabsCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Polls <paramref name="condition"/> until it holds or the budget runs out (the class field <c>Wait</c> hides the shared helper).</summary>
    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20, Cancel);
        }

        return condition();
    }

    /// <summary>A runtime directory short enough for the endpoint inside it to fit in <c>sun_path</c>.</summary>
    private static string ShortDirectory()
    {
        var path = Path.Combine("/tmp", "lc-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ExportBackup(TestConsole console)
    {
        var path = Path.Combine(TestConsole.TempDirectory(), "room.lcbak");
        Assert.True(console.Bootstrap.TryExportBackup(console.Session, path, out var error), error);
        return path;
    }

    private static string JsonQuoted(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>A raw connection to the endpoint: what an attacker, not a console, would open.</summary>
    private static async Task<Stream> ConnectRawAsync(string dataDirectory, CancellationToken cancellation)
    {
        var endpoint = SingleInstance.EndpointFor(dataDirectory);
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000, cancellation);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellation);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Sends <paramref name="request"/> verbatim and returns the answer line, or <c>null</c> when the console said nothing.</summary>
    private static async Task<string?> AskRawAsync(string dataDirectory, byte[] request, CancellationToken cancellation)
    {
        await using var stream = await ConnectRawAsync(dataDirectory, cancellation);
        try
        {
            await stream.WriteAsync(request, cancellation);
            await stream.FlushAsync(cancellation);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // The console hung up in the middle of the request (past the line cap): no answer.
            return null;
        }

        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellation);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            if (Array.IndexOf(chunk, (byte)'\n', 0, read) >= 0)
            {
                break;
            }
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\n');
        return text.Length == 0 ? null : text;
    }

    /// <summary>A console as the app runs one: the data directory's lock, and the endpoint that lock entitles it to.</summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly ConsoleLock _held;
        private readonly Lock _gate = new();
        private readonly List<IReadOnlyList<string>> _arrived = [];
        private bool _disposed;

        private TestServer(ConsoleLock held, SingleInstance instance)
        {
            _held = held;
            Instance = instance;
        }

        public SingleInstance Instance { get; }

        public string Endpoint => Instance.Endpoint;

        public TaskCompletionSource<IReadOnlyList<string>> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<IReadOnlyList<string>> Arrived
        {
            get
            {
                lock (_gate)
                {
                    return [.. _arrived];
                }
            }
        }

        public static TestServer Start(string dataDirectory, ILogger? log = null)
        {
            var held = ConsoleLock.TryAcquire(dataDirectory, out var error);
            Assert.NotNull(held);
            Assert.Equal(string.Empty, error);
            var instance = SingleInstance.Listen(dataDirectory, held, action => action(), log);
            var server = new TestServer(held, instance);
            instance.FilesArrived += files =>
            {
                lock (server._gate)
                {
                    server._arrived.Add(files);
                }

                server.First.TrySetResult(files);
            };
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Instance.DisposeAsync();
            _held.Dispose();
        }
    }

    /// <summary>
    /// Dialogs that stop each import batch at its results window until the test lets it
    /// through, so two batches can be caught overlapping if they ever do.
    /// </summary>
    private sealed class GatedDialogs : IDialogs
    {
        private readonly SemaphoreSlim _gate = new(0);
        private int _batches;

        public int Batches => Volatile.Read(ref _batches);

        public void LetOneThrough() => _gate.Release();

        public async Task ShowImportResultsAsync(IReadOnlyList<ImportFileResult> results)
        {
            Interlocked.Increment(ref _batches);
            await _gate.WaitAsync();
        }

        public Task<UnlockAnswer?> UnlockAsync(string reason) => Task.FromResult<UnlockAnswer?>(null);

        public Task<HolderAnswer?> AddHolderAsync() => Task.FromResult<HolderAnswer?>(null);

        public Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false) => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowRecoveryCodeAsync(RecoveryCode code) => Task.FromResult(false);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult<string?>(null);

        public Task<string?> PickOpenFileAsync(string title, string extension) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<SendFilesAnswer?> SendFilesAsync(int pcCount) => Task.FromResult<SendFilesAnswer?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<AgentBuild?> PushAgentBuildAsync(int pcCount) => Task.FromResult<AgentBuild?>(null);

        public void ShowScreen(ScreenViewModel screen)
        {
        }
    }
}

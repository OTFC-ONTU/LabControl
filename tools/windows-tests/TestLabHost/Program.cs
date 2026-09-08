using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging.Abstractions;

// Disposable development fixture. Never accepts an existing lab/data path.
return await TestLabHost.RunAsync(args);

internal static class TestLabHost
{
    private const string SessionMarker = "LABCONTROL_M4_SESSION_SCRIPT_OK";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public static async Task<int> RunAsync(string[] args)
    {
        try { return await RunCoreAsync(args); }
        catch (Exception error)
        {
            // Native/transport exceptions can contain external data: report only the type.
            System.Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.GetType().Name }, Json));
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This fixture host is for macOS only.");
        var bind = IPAddress.Loopback;
        var port = 0;
        var duration = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option) || ++i == args.Length) throw new ArgumentException("Invalid option.");
            switch (option)
            {
                case "--bind": bind = IPAddress.Parse(args[i]); break;
                case "--port": port = int.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--duration-seconds": duration = int.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException("Unknown option.");
            }
        }
        if (bind.AddressFamily != AddressFamily.InterNetwork || bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.Broadcast)
            || port is < 0 or > 65535 || port == Defaults.ConsolePort || duration is < 0 or > 86400)
            throw new ArgumentException("Use an explicit IPv4 interface and a nonproduction port.");

        var root = Directory.CreateTempSubdirectory("labcontrol-m4-disposable-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var data = Path.Combine(root, "console");
        var inbox = Path.Combine(root, "commands");
        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(results);
        var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var key = LabKey.Create("Disposable M4 fixture", "Disposable fixture", passphrase, out _);
        var store = new LabStore(data);
        store.EnsureDirectories();
        store.SaveLabKey(key.Document);
        var instance = ConsoleInstance.Mint(key, "Disposable M4 fixture", new FileSecretProtector());
        store.SaveInstance(instance.Document);
        var vault = new LabKeyVault(store, key.Document);
        vault.Adopt(key);
        var beaconPort = ReserveBeaconPort();
        await using var session = new LabSession(new ConsoleOptions
        {
            DataDirectory = data, BindAddress = bind, Port = port, BeaconPort = beaconPort,
        }, store, vault, instance, instance.Document, NullLoggerFactory.Instance);
        await session.StartAsync();
        if (session.Port == Defaults.ConsolePort) throw new IOException("An ephemeral port matched production.");
        var usb = Path.Combine(root, "usb");
        var payloadPath = session.WritePayload(usb, 1);
        var payload = SetupPayload.Open(payloadPath);
        using (payload.Authority)
        {
            payload.Document.ConsoleHost = bind.ToString();
            JsonStore.Save(Path.Combine(payloadPath, Defaults.SetupFileName), payload.Document, SetupPayloadDocument.Migrations);
        }
        using var stopping = new CancellationTokenSource();
        if (duration > 0) stopping.CancelAfter(TimeSpan.FromSeconds(duration));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stopping.Cancel(); };
        System.Console.CancelKeyPress += cancel;
        var sessionScriptJobs = new Dictionary<string, string>(StringComparer.Ordinal);
        var ready = new { state = "ready", root, payload = payloadPath, commands = inbox, status = Path.Combine(root, "status.json"),
            lab_id = session.LabId, host = bind.ToString(), port = session.Port, beacon_port = beaconPort };
        System.Console.WriteLine(JsonSerializer.Serialize(ready, Json));
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                foreach (var path in Directory.EnumerateFiles(inbox, "*.json").Order(StringComparer.Ordinal).Take(16))
                {
                    var filename = Path.GetFileNameWithoutExtension(path);
                    if (!Guid.TryParseExact(filename, "N", out _)) continue;
                    var working = path + ".working";
                    File.Move(path, working, overwrite: false);
                    object result;
                    try
                    {
                        if ((File.GetAttributes(working) & FileAttributes.ReparsePoint) != 0 || new FileInfo(working).Length > 8192)
                            throw new InvalidDataException("Invalid command file.");
                        var command = JsonSerializer.Deserialize<Command>(File.ReadAllText(working), Json)
                            ?? throw new InvalidDataException("Invalid command.");
                        switch (command.Action)
                        {
                            case "push":
                            case "push-wrong-key":
                                if (command.AgentId is null || !session.IsLinked(command.AgentId))
                                    throw new InvalidOperationException("Choose a linked fixture agent.");
                                if (command.BuildDirectory is null || command.BaseVersion is null
                                    || !AgentBuild.TryLoad(command.BuildDirectory, command.BaseVersion, out var build, out _, command.MinimumInstalledVersion ?? "0.0.0"))
                                    throw new InvalidDataException("Invalid update build.");
                                if (!vault.IsUnlocked && !vault.TryUnlock(passphrase)) throw new InvalidOperationException("Fixture key is unavailable.");
                                IReadOnlyList<JobRecord> jobs;
                                if (command.Action == "push-wrong-key")
                                {
                                    using var wrongKey = LabKey.Create("Wrong signing authority fixture", "Disposable fixture",
                                        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), out _);
                                    foreach (var file in build.Files) session.Files.OfferFile(file.Path);
                                    var manifest = session.Files.OfferBytes(build.Manifest, "wrong-key-manifest");
                                    var request = new SelfUpdateRequest(build.Version, manifest.Reference, manifest.Sha256,
                                        UpdateManifestSignature.Sign(wrongKey, build.Manifest));
                                    jobs = session.CreateJobs([command.AgentId], LabControl.Shared.Protocol.Job.Types.Kind.SelfUpdate,
                                        request.ToArgs(), Defaults.SelfUpdateJobTimeout);
                                }
                                else jobs = session.PushAgentBuild([command.AgentId], build);
                                result = new { ok = true, action = command.Action, version = build.Version, job_ids = jobs.Select(job => job.Id).ToArray() };
                                break;
                            case "handouts":
                                if (command.AgentId is null || !session.IsLinked(command.AgentId)
                                    || command.Paths is not { Length: > 0 and <= 8 })
                                    throw new InvalidOperationException("Choose a linked fixture agent and bounded fixture files.");
                                var handoutJobs = session.SendFiles([command.AgentId], command.Paths, command.Open);
                                result = new { ok = true, action = command.Action, job_ids = handoutJobs.Select(job => job.Id).ToArray() };
                                break;
                            case "session-script":
                                if (command.AgentId is null || !session.IsLinked(command.AgentId))
                                    throw new InvalidOperationException("Choose a linked fixture agent.");
                                var script = new ScriptRecord
                                {
                                    Name = "M4 session smoke", RunAs = RunScriptRequest.UserValue,
                                    Shell = RunScriptRequest.PowerShellValue, TimeoutSeconds = 30,
                                    Text = "if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) { exit 73 }; Write-Output '" + SessionMarker + "'; exit 0",
                                };
                                var scriptJobs = session.RunScript([command.AgentId], script);
                                foreach (var job in scriptJobs) sessionScriptJobs.Add(job.Id, job.BatchId);
                                result = new { ok = true, action = "session-script", job_ids = scriptJobs.Select(job => job.Id).ToArray() };
                                break;
                            case "pointer-smoke":
                                if (command.AgentId is null || !session.IsLinked(command.AgentId))
                                    throw new InvalidOperationException("Choose a linked fixture agent.");
                                // No clicks or keystrokes: a native observer checks the normalized cursor position.
                                var sent = session.SendInput(command.AgentId, new LabControl.Shared.Protocol.Input
                                {
                                    Kind = LabControl.Shared.Protocol.Input.Types.Kind.MouseMove, X = 0.25, Y = 0.75,
                                });
                                result = new { ok = sent, action = "pointer-smoke", x = 0.25, y = 0.75 };
                                break;
                            case "unlock":
                                result = new { ok = vault.IsUnlocked || vault.TryUnlock(passphrase), action = "unlock" };
                                break;
                            case "status": result = new { ok = true, action = "status" }; break;
                            case "stop": stopping.Cancel(); result = new { ok = true, action = "stop" }; break;
                            default: throw new InvalidDataException("Unknown fixture action.");
                        }
                    }
                    catch (Exception error) { result = new { ok = false, error = error.GetType().Name }; }
                    WriteJson(Path.Combine(results, filename + ".json"), result);
                    File.Delete(working);
                }
                WriteJson(Path.Combine(root, "status.json"), new
                {
                    state = stopping.IsCancellationRequested ? "stopping" : "serving", utc = DateTimeOffset.UtcNow,
                    lab_id = session.LabId, host = bind.ToString(), port = session.Port, beacon_port = beaconPort,
                    lab_key_unlocked = vault.IsUnlocked,
                    agents = session.Linked.Select(pc => new { agent_id = pc.AgentId, number = pc.Number, version = pc.Hello.AgentVersion,
                        update_phase = pc.UpdateState.Phase.ToString(), failed_version = pc.UpdateState.FailedVersion,
                        probation_ends_unix = pc.UpdateState.ProbationEndsUnix, heartbeat = pc.LastHeartbeat,
                        helper_alive = pc.HelperAlive, session_id = pc.SessionId, session_locked = pc.SessionLocked,
                        no_session = pc.NoSession, capture_problem_reported = pc.CaptureProblem is not null,
                        readiness_codes = pc.Machine.SetupReadinessCodes,
                        screen = ScreenStatus(session.Screens.Find(pc.AgentId)) }).ToArray(),
                    jobs = session.Jobs.All().Select(job => new { id = job.Id, agent_id = job.AgentId, state = job.State.ToString(),
                        ok = job.Ok, percent = job.Percent, exit_code = job.ExitCode,
                        session_marker_received = sessionScriptJobs.TryGetValue(job.Id, out var batch)
                            && session.Jobs.SnapshotBatch(batch).Any(snapshot => snapshot.Id == job.Id && snapshot.Output.Contains(SessionMarker, StringComparer.Ordinal)) }).ToArray(),
                });
                try { await Task.Delay(500, stopping.Token); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            System.Console.CancelKeyPress -= cancel;
            WriteJson(Path.Combine(root, "status.json"), new { state = "stopped", lab_id = session.LabId, utc = DateTimeOffset.UtcNow });
        }
        return 0;
    }

    private static object? ScreenStatus(AgentScreen? screen) => screen is null ? null : new
    {
        has_thumbnail = screen.Thumbnail.HasFrame,
        thumbnail_width = screen.Thumbnail.Width, thumbnail_height = screen.Thumbnail.Height,
        thumbnail_version = screen.Thumbnail.Version,
        last_frame_at = screen.LastFrameAt, frames_received = screen.FramesReceived,
        frames_rejected = screen.FramesRejected, screen_width = screen.ScreenWidth, screen_height = screen.ScreenHeight,
    };

    private static int ReserveBeaconPort()
    {
        while (true)
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            if (port != Defaults.BeaconPort) return port;
        }
    }
    private static void WriteJson(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
    private sealed record Command(string Action, string? AgentId, string? BuildDirectory, string? BaseVersion,
        string? MinimumInstalledVersion, string[]? Paths, bool Open);
}

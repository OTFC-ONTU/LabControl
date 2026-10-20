using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Discovery;
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
    private const string OwnershipMarker = "LABCONTROL_M5_RESULT_OWNERSHIP_OK";
    private const string RootMarkerFileName = "fixture-root.json";

    /// <summary>A short alphanumeric directory name, so a fixture argument can never walk out of the root.</summary>
    private static bool IsFixtureName(string? value) =>
        value is { Length: > 0 and <= 32 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-');

    private static string Trim(string value) => value.Length <= 200 ? value : value[..200];

    /// <summary>
    /// A leaf with <c>OU=LabControl Teacher</c> (D-56 items 4–5) for this disposable lab, so the
    /// fixture can offer an agent a console whose certificate carries teacher access and nothing
    /// else. The request never leaves this process; the key is protected like any instance key.
    /// </summary>
    private static InstanceDocument MintTeacherDevice(LabKey lab, string instanceName)
    {
        var instanceId = Guid.NewGuid().ToString("d");
        using var key = LabCertificates.CreateKey();
        var request = LabCertificates.CreateDeviceSigningRequest(key, instanceName);
        using var certificate = LabCertificates.IssueTeacherDevice(lab.Authority, lab.LabId, instanceId, instanceName, request, DateTimeOffset.UtcNow);
        return new InstanceDocument
        {
            LabId = lab.LabId,
            InstanceId = instanceId,
            InstanceName = instanceName,
            Certificate = certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert),
            Endorsement = Beacon.Endorse(lab, instanceId, P256.Compress(key)),
            PrivateKey = new FileSecretProtector().Protect(ConsoleInstance.ProtectionReference(instanceId), key.ExportPkcs8PrivateKey()),
            CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

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
        string? reuseRoot = null;
        var dataName = "console";
        var teacherLeaf = false;
        var broadcastSource = "synthetic";
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
                case "--root": reuseRoot = Path.GetFullPath(args[i]); break;
                case "--instance": dataName = args[i]; break;
                // Presents a teacher-access leaf (D-56 item 5) while still holding the key, so the
                // agent's refusal can be observed. No production console is ever built this way.
                case "--teacher-leaf": teacherLeaf = bool.Parse(args[i]); break;
                case "--broadcast-source": broadcastSource = args[i] is "synthetic" or "screen" ? args[i] : throw new ArgumentException("Invalid option."); break;
                default: throw new ArgumentException("Unknown option.");
            }
        }

        if (!IsFixtureName(dataName)) throw new ArgumentException("An instance directory name is short and alphanumeric.");
        if (bind.AddressFamily != AddressFamily.InterNetwork || bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.Broadcast)
            || port is < 0 or > 65535 || port == Defaults.ConsolePort || duration is < 0 or > 86400)
            throw new ArgumentException("Use an explicit IPv4 interface and a nonproduction port.");

        string root;
        if (reuseRoot is not null)
        {
            // Only a root this fixture wrote, so --root can never be pointed at real console data.
            if (!Directory.Exists(reuseRoot) || !File.Exists(Path.Combine(reuseRoot, RootMarkerFileName)))
                throw new ArgumentException("--root takes a disposable root this fixture created earlier.");
            root = reuseRoot;
        }
        else
        {
            root = Directory.CreateTempSubdirectory("labcontrol-m4-disposable-").FullName;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            WriteJson(Path.Combine(root, RootMarkerFileName), new { fixture = "labcontrol-disposable-lab-host", created_utc = DateTimeOffset.UtcNow });
        }

        var data = Path.Combine(root, dataName);
        var inbox = Path.Combine(root, dataName + ".commands");
        var results = Path.Combine(root, dataName + ".results");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(results);
        var store = new LabStore(data);
        string? passphrase = null;
        LabKeyVault vault;
        ConsoleInstance instance;
        var createdLab = !store.HasLab;
        if (createdLab)
        {
            passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            var key = LabKey.Create("Disposable M4 fixture", "Disposable fixture", passphrase, out _);
            store.EnsureDirectories();
            store.SaveLabKey(key.Document);
            var minted = teacherLeaf
                ? MintTeacherDevice(key, "Disposable M4 fixture teacher " + dataName)
                : ConsoleInstance.Mint(key, "Disposable M4 fixture " + dataName, new FileSecretProtector()).Document;
            store.SaveInstance(minted);
            instance = ConsoleInstance.Open(minted);
            vault = new LabKeyVault(store, key.Document);
            vault.Adopt(key);
        }
        else
        {
            // Started again, or the second console instance this lab was forked into: the lab
            // key stays locked, exactly like a teacher machine that only links to PCs.
            var saved = store.LoadInstance() ?? throw new InvalidDataException("This disposable console directory has no instance.");
            instance = ConsoleInstance.Open(saved);
            vault = new LabKeyVault(store, store.LoadLabKey());
        }

        var beaconPort = ReserveBeaconPort();
        await using var session = new LabSession(new ConsoleOptions
        {
            DataDirectory = data, BindAddress = bind, Port = port, BeaconPort = beaconPort,
        }, store, vault, instance, instance.Document, NullLoggerFactory.Instance);
        if (broadcastSource == "synthetic") session.BroadcastSource = () => new SyntheticBroadcastSource();
        await session.StartAsync();
        if (session.Port == Defaults.ConsolePort) throw new IOException("An ephemeral port matched production.");
        var usb = Path.Combine(root, "usb");
        // Enrolment codes are single use: only the run that creates the lab writes the payload.
        var payloadPath = Path.Combine(usb, Defaults.PayloadDirectoryName);
        if (createdLab)
        {
            payloadPath = session.WritePayload(usb, 1);
            var payload = SetupPayload.Open(payloadPath);
            using (payload.Authority)
            {
                payload.Document.ConsoleHost = bind.ToString();
                JsonStore.Save(Path.Combine(payloadPath, Defaults.SetupFileName), payload.Document, SetupPayloadDocument.Migrations);
            }
        }
        using var stopping = new CancellationTokenSource();
        if (duration > 0) stopping.CancelAfter(TimeSpan.FromSeconds(duration));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stopping.Cancel(); };
        System.Console.CancelKeyPress += cancel;
        var sessionScriptJobs = new Dictionary<string, string>(StringComparer.Ordinal);
        var ready = new { state = "ready", root, payload = payloadPath, commands = inbox, results, status = Path.Combine(root, dataName + ".status.json"),
            lab_id = session.LabId, instance = dataName, instance_id = instance.InstanceId, created_lab = createdLab,
            host = bind.ToString(), port = session.Port, beacon_port = beaconPort };
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
                                if (!vault.IsUnlocked && !vault.TryReopen() && (passphrase is null || !vault.TryUnlock(passphrase))) throw new InvalidOperationException("Fixture key is unavailable.");
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
                            case "long-script":
                                // D-57 item 4: a script that outlives a dropped link, so its result
                                // has to wait on the PC for the console instance that delivered it.
                                if (command.AgentId is null || !session.IsLinked(command.AgentId))
                                    throw new InvalidOperationException("Choose a linked fixture agent.");
                                if (command.Seconds is not (>= 5 and <= 600))
                                    throw new InvalidDataException("Choose a bounded number of seconds.");
                                var slow = new ScriptRecord
                                {
                                    Name = "M5 result ownership", RunAs = RunScriptRequest.SystemValue,
                                    Shell = RunScriptRequest.PowerShellValue, TimeoutSeconds = command.Seconds.Value + 120,
                                    // One line before the wait and one after it: the first reaches the
                                    // console that sent the job, the second falls while it is away.
                                    Text = "Write-Output '" + OwnershipMarker + "-START'; Start-Sleep -Seconds "
                                           + command.Seconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                           + "; Write-Output '" + OwnershipMarker + "-END'; exit 57",
                                };
                                var slowJobs = session.RunScript([command.AgentId], slow);
                                result = new { ok = true, action = "long-script", seconds = command.Seconds.Value,
                                    job_ids = slowJobs.Select(job => job.Id).ToArray() };
                                break;
                            case "fork":
                                // A second teacher machine of the same disposable lab: its own console
                                // instance, minted here because only this run holds the passphrase.
                                if (passphrase is null || !vault.IsUnlocked && !vault.TryUnlock(passphrase))
                                    throw new InvalidOperationException("Only the run that created this lab can mint another instance.");
                                if (!IsFixtureName(command.Instance)) throw new InvalidDataException("Choose a short alphanumeric instance directory.");
                                var forkPath = Path.Combine(root, command.Instance!);
                                if (Directory.Exists(forkPath)) throw new IOException("That fixture instance directory already exists.");
                                Directory.CreateDirectory(forkPath);
                                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(forkPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                                foreach (var file in Directory.EnumerateFiles(data))
                                    File.Copy(file, Path.Combine(forkPath, Path.GetFileName(file)), overwrite: false);
                                var forkStore = new LabStore(forkPath);
                                forkStore.EnsureDirectories();
                                if (!vault.Use(key => command.Teacher
                                        ? MintTeacherDevice(key, "Disposable M4 fixture " + command.Instance)
                                        : ConsoleInstance.Mint(key, "Disposable M4 fixture " + command.Instance, new FileSecretProtector()).Document,
                                        out var forked))
                                    throw new InvalidOperationException("Fixture key is unavailable.");
                                forkStore.SaveInstance(forked);
                                result = new { ok = true, action = "fork", instance = command.Instance, instance_id = forked.InstanceId,
                                    teacher = command.Teacher, directory = forkPath };
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
                            case "lock-screens":
                            case "unlock-screens":
                            case "full-screen":
                            case "input-text":
                            case "secure-attention":
                            case "escape-key":
                                if (command.AgentId is null || !session.IsLinked(command.AgentId))
                                    throw new InvalidOperationException("Choose a linked fixture agent.");
                                if (command.Action == "lock-screens")
                                {
                                    if (command.Seconds is < 0 or > 3600) throw new ArgumentException("Invalid lock duration.");
                                    result = new { ok = session.Lock([command.AgentId], "Перевірка блокування — зачекайте", command.Seconds is > 0 ? TimeSpan.FromSeconds(command.Seconds.Value) : null) == 1 };
                                }
                                else if (command.Action == "unlock-screens")
                                    result = new { ok = session.Unlock([command.AgentId]) == 1 };
                                else if (command.Action == "full-screen")
                                {
                                    session.SetScreenMode(command.AgentId, LabControl.Shared.Protocol.VideoMode.Full);
                                    result = new { ok = true };
                                }
                                else if (command.Action == "escape-key")
                                {
                                    var down = session.SendInput(command.AgentId, LabControl.Shared.Control.InputMessages.Key(LabControl.Shared.Control.InputMessages.VkEscape, 0, true));
                                    var up = session.SendInput(command.AgentId, LabControl.Shared.Control.InputMessages.Key(LabControl.Shared.Control.InputMessages.VkEscape, 0, false));
                                    result = new { ok = down && up };
                                }
                                else
                                    result = new { ok = session.SendInput(command.AgentId, command.Action == "input-text"
                                        ? LabControl.Shared.Control.InputMessages.Text("M6_REMOTE_INPUT_OK")
                                        : LabControl.Shared.Control.InputMessages.CtrlAltDel()) };
                                break;
                            case "broadcast-start":
                                if (command.AgentId is null || !session.IsLinked(command.AgentId))
                                    throw new InvalidOperationException("Choose a linked fixture agent.");
                                result = new { ok = session.StartBroadcast([command.AgentId]) == 1, source = broadcastSource };
                                break;
                            case "broadcast-stop":
                                await session.StopBroadcastAsync();
                                result = new { ok = true };
                                break;
                            case "save-screen":
                                if (command.AgentId is null || !IsFixtureName(command.Instance)) throw new InvalidDataException("Choose an agent and a short name.");
                                using (var snapshot = session.Screens.Find(command.AgentId)?.Full.Snapshot())
                                {
                                    if (snapshot is null) { result = new { ok = false, error = "no full frame" }; break; }
                                    Directory.CreateDirectory(Path.Combine(root, "shots"));
                                    var shotPath = Path.Combine(root, "shots", command.Instance + ".png");
                                    using var encoded = snapshot.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                                    using (var output = File.Create(shotPath)) encoded.SaveTo(output);
                                    result = new { ok = true, path = shotPath, width = snapshot.Width, height = snapshot.Height };
                                }
                                break;
                            case "broadcast-freeze":
                            case "broadcast-thaw":
                                SyntheticBroadcastSource.Frozen = command.Action == "broadcast-freeze";
                                result = new { ok = true, frozen = SyntheticBroadcastSource.Frozen };
                                break;
                            case "broadcast-access":
                                result = new { ok = true, access = LabControl.Shared.Video.ConsoleScreen.Access().ToString() };
                                break;
                            // M6 portion 3 internet drill: block, whitelist (hosts in `paths`), open, allow one refused name.
                            case "internet-block":
                            case "internet-whitelist":
                            case "internet-open":
                                if (command.AgentId is null || !session.IsLinked(command.AgentId))
                                    throw new InvalidOperationException("Choose a linked fixture agent.");
                                if (command.Seconds is < 0 or > 28800) throw new ArgumentException("Invalid internet duration.");
                                if (command.Action == "internet-open")
                                    result = new { ok = session.LiftInternet([command.AgentId]) == 1 };
                                else
                                {
                                    var hosts = command.Action == "internet-whitelist" ? (command.Paths ?? []).Take(32).ToArray() : [];
                                    result = new { ok = session.SetInternet([command.AgentId],
                                        command.Action == "internet-block" ? LabControl.Shared.Protocol.InternetPolicy.Types.Mode.Blocked : LabControl.Shared.Protocol.InternetPolicy.Types.Mode.Whitelist,
                                        hosts, command.Action == "internet-whitelist" ? "Drill" : string.Empty,
                                        command.Seconds is > 0 ? TimeSpan.FromSeconds(command.Seconds.Value) : null) == 1 };
                                }
                                break;
                            case "internet-allow":
                                if (command.Paths is not [var name]) throw new ArgumentException("Name one host in paths.");
                                result = new { ok = true, asked = session.AllowRefusedName(name) };
                                break;
                            // D-82 drill: a new lab B in this root (instance directory "moved"), remembered with the
                            // file protector, and the PC sent its move. Start a second host with
                            // --root <root> --instance moved --port <this port> to serve B afterwards.
                            case "move-new-key":
                            {
                                if (command.AgentId is null || !session.IsLinked(command.AgentId))
                                    throw new InvalidOperationException("Choose a linked fixture agent.");
                                var movedStore = new LabStore(Path.Combine(root, "moved"));
                                if (movedStore.HasLab) throw new InvalidOperationException("This root already holds a moved lab.");
                                using var moved = LabKey.Create("Disposable M4 fixture moved", "Disposable fixture", Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), out _);
                                movedStore.EnsureDirectories();
                                movedStore.SaveLabKey(moved.Document);
                                var movedInstance = ConsoleInstance.Mint(moved, "Disposable M4 fixture moved", new FileSecretProtector());
                                movedStore.SaveInstance(movedInstance.Document);
                                movedStore.SaveLab(new LabDocument { LabId = moved.LabId, LabName = moved.LabName });
                                new LabKeyMemory(movedStore, movedInstance.InstanceId, () => new FileSecretProtector()).Remember(moved);
                                movedStore.SaveLabKey(moved.Document);
                                var moveJobs = session.MoveToNewKey([command.AgentId], new LabMoveTarget(moved.LabId, moved.LabName, movedStore, moved.Document.Authority));
                                result = new { ok = moveJobs.Count == 1, moved_lab_id = moved.LabId, job_ids = moveJobs.Select(j => j.Id).ToArray() };
                                break;
                            }
                            case "unlock":
                                result = new { ok = vault.IsUnlocked || passphrase is not null && vault.TryUnlock(passphrase), action = "unlock" };
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
                WriteJson(Path.Combine(root, dataName + ".status.json"), new
                {
                    state = stopping.IsCancellationRequested ? "stopping" : "serving", utc = DateTimeOffset.UtcNow,
                    lab_id = session.LabId, instance = dataName, instance_id = instance.InstanceId,
                    host = bind.ToString(), port = session.Port, beacon_port = beaconPort,
                    lab_key_unlocked = vault.IsUnlocked,
                    broadcast_targets = session.BroadcastTargets.ToArray(), broadcasting = session.Broadcast is not null,
                    refused_names = session.RefusedNames().Select(r => new { r.Name, r.PcCount }).ToArray(),
                    events = session.Events.Recent.TakeLast(25)
                        .Select(e => new { at = e.AtUnix, severity = e.Severity.ToString(), code = e.Code, message = Trim(e.Message) }).ToArray(),
                    agents = session.Linked.Select(pc => new { agent_id = pc.AgentId, number = pc.Number, version = pc.Hello.AgentVersion,
                        update_phase = pc.UpdateState.Phase.ToString(), failed_version = pc.UpdateState.FailedVersion,
                        probation_ends_unix = pc.UpdateState.ProbationEndsUnix, heartbeat = pc.LastHeartbeat,
                        helper_alive = pc.HelperAlive, session_id = pc.SessionId, session_locked = pc.SessionLocked,
                        no_session = pc.NoSession, capture_problem_reported = pc.CaptureProblem is not null,
                        overlay = pc.OverlayState is { } overlay ? new { kind = overlay.Kind.ToString(), overlay.SessionId,
                            overlay.InputBlocked, overlay.Reason, overlay.Problem, overlay.RemainingSeconds,
                            overlay.SetByInstance, overlay.SinceUnix, overlay.UntilUnix, overlay.HardLimitUnix } : null,
                        internet = pc.InternetState is { } internet ? new { mode = internet.Mode.ToString(), internet.SessionId,
                            internet.UntilUnix, internet.HardLimitUnix, internet.RemainingSeconds, internet.ResolverActive,
                            allowed = internet.AllowedHosts.ToArray(), refused = internet.RefusedRecently.ToArray(),
                            internet.Problem, internet.Reason, internet.SetByInstance } : null,
                        internet_problem = pc.InternetProblem,
                        readiness_codes = pc.Machine.SetupReadinessCodes,
                        screen = ScreenStatus(session.Screens.Find(pc.AgentId)) }).ToArray(),
                    jobs = session.Jobs.All().Select(job => new { id = job.Id, agent_id = job.AgentId, state = job.State.ToString(),
                        kind = job.Kind.ToString(), job_instance_id = job.InstanceId,
                        ok = job.Ok, percent = job.Percent, exit_code = job.ExitCode, message = Trim(job.Message),
                        output = job.Output.Take(5).Select(Trim).ToArray(),
                        ownership_marker_received = job.Output.Any(line => line.Contains(OwnershipMarker, StringComparison.Ordinal)),
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
            WriteJson(Path.Combine(root, dataName + ".status.json"), new { state = "stopped", lab_id = session.LabId, instance = dataName, utc = DateTimeOffset.UtcNow });
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
        string? MinimumInstalledVersion, string[]? Paths, bool Open, int? Seconds, string? Instance, bool Teacher);
}

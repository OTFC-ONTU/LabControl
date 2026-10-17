using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

internal static class SetupCoordinator
{
    public static IDisposable AcquireProcessLock()
    {
        var mutex = new Mutex(false, Defaults.SetupProcessMutexName);
        bool held;
        try { held = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) { mutex.Dispose(); throw new IOException("Another Setup or removal process is running."); }
        return new MutexLease(mutex);
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    public static int Rekey(SetupArguments arguments, string payloadDirectory)
    {
        Preflight();
        if (arguments.DryRun)
        {
            Console.WriteLine("[plan] Stop the service, replace only lab trust while retaining this PC identity, then restart.");
            return 0;
        }
        using var scope = AccountSetupScope.Open();
        var installation = new InstallationState(Defaults.AgentDataDirectory).Read()
            ?? throw new InvalidOperationException("Rekey requires an owned installation.");
        if (installation.RemovalReady || installation.StudentRemovalPending || installation.StudentRemoved)
            throw new InvalidOperationException("Finish the pending uninstall before replacing trust.");
        if (new WindowsSetupService(installation.InstallationId).Read() is null)
            throw new InvalidOperationException("Rekey requires the owned agent service.");
        using var updateLease = new UpdateTrial(Defaults.AgentDataDirectory).AcquireRemovalLease(out _);
        var replacement = new AgentTrustRekey(Defaults.AgentDataDirectory, MachineKeyProtection.Dpapi, MachineKeyProtection.RekeyJournal);
        SetupPayload? payload = replacement.HasPending ? null : SetupPayload.Open(payloadDirectory);
        using var authority = payload?.Authority;
        WindowsSetupService.Stop();
        try
        {
            if (replacement.HasPending) replacement.Resume();
            else replacement.Replace(payload!);
            using var store = DirectoryAgentStore.Open(Defaults.AgentDataDirectory, MachineKeyProtection.Dpapi);
            Console.WriteLine("Lab trust replaced; the PC identity, account mode and machine settings were preserved.");
        }
        finally
        {
            // A partial transaction must remain offline; Open also refuses mixed trust
            // after reboot. Re-running rekey resumes it without the USB or another code.
            if (!replacement.HasPending) WindowsSetupService.Start();
        }
        return 0;
    }

    public static int Install(SetupArguments arguments, int number, bool? createStudent, string payloadDirectory)
    {
        Preflight();
        var files = new SetupInstallationFiles(payloadDirectory);
        files.ValidateSource();
        if (InstallLayout.Default.ReadCurrent() is { } currentVersion
            && Version.TryParse(InstallLayout.BaseVersionOf(currentVersion), out var installedBase)
            && Version.TryParse(InstallLayout.BaseVersionOf(files.Version), out var usbBase) && usbBase < installedBase)
            throw new InvalidOperationException("This USB is older than the installed agent. Use the same or a newer USB build for repair.");
        var payload = SetupPayload.Open(payloadDirectory);
        using var authority = payload.Authority;
        var state = new InstallationState(Defaults.AgentDataDirectory);
        var existing = state.Read();
        var legacy = existing is null && (DirectoryAgentStore.Exists(Defaults.AgentDataDirectory) || Directory.Exists(Defaults.AgentInstallDirectory));
        if (legacy) throw new InvalidOperationException("This is an earlier development installation without installer ownership. Preserve it and use a fresh test snapshot for Setup.");
        if (existing is { RemovalReady: true } or { StudentRemovalPending: true } or { StudentRemoved: true })
            throw new InvalidOperationException("Finish removal before installing or repairing this PC.");
        if (number < 1 || number > Defaults.MaxStudentPcs || existing?.Number is { } installedNumber && installedNumber != number)
            throw new InvalidOperationException("The PC number is invalid or differs from this installation.");
        var desiredName = AgentProvisioning.NameOf(number);
        var hostname = new WindowsHostnameSystem().Read();
        if (hostname.RebootRequired && !string.Equals(hostname.Pending, desiredName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(SetupDialog.Text("PendingHostnameChange"));
        var selectedStudent = createStudent ?? existing?.CreateStudentAccount ?? true;
        if (selectedStudent)
        {
            var currentSid = new WindowsStudentAccountSystem().FindStudentSid();
            if (existing?.CreatedStudentSid is { } owned ? currentSid != owned : currentSid is not null)
                throw new InvalidOperationException("The student account is not owned by this installation; choose account creation off for a fresh installation.");
        }
        Console.WriteLine(SetupDialog.Text("Changes"));
        if (arguments.DryRun)
        {
            foreach (var name in new[] { "Private ownership journal", "PC name", "Files", "Enrollment", "Firewall", "Power", "Windows Update active hours", "Student account and sign-in", "Service", "Verification" })
                Console.WriteLine($"[plan] {name}");
            if (!selectedStudent) Console.WriteLine("[Skipped] All account, password, sign-in and profile steps: account creation is off.");
            return 0;
        }
        SetupVersionUpgrade.VerifyExecutable(files.SourceAgentExecutable, files.Version);
        using var scope = AccountSetupScope.Open();
        existing = state.Read();
        if (new UpdateTrial(Defaults.AgentDataDirectory).Read()?.Phase is UpdateTrialPhase.OnProbation or UpdateTrialPhase.RollbackPending)
            throw new InvalidOperationException("Finish agent update recovery before USB installation or repair.");
        state.Configure(existingInstallation: existing is not null, createStudent);
        state.SelectNumber(number);
        scope.EnsureFreshSettingsJournal();
        files.InstallationId = state.Read()!.InstallationId;
        if (files.Check().Status == SetupStepStatus.Conflict)
            throw new IOException("The installed files conflict with this USB; existing files were preserved.");
        var templatePath = state.Read()!.CreateStudentAccount == true
            ? Path.Combine(payloadDirectory, Defaults.ProfileTemplateArchiveFileName) : null;
        if (templatePath is not null && File.Exists(templatePath))
        {
            SetupInstallationFiles.Guard(templatePath);
            using var archive = File.OpenRead(templatePath);
            _ = ProfileTemplateArchive.Read(archive);
        }
        else templatePath = null;
        using var log = new StreamWriter(Path.Combine(Defaults.AgentDataDirectory, Defaults.SetupLogFileName), append: true) { AutoFlush = true };
        void Report(string text) { log.WriteLine(text); Console.WriteLine(text); }
        var setupVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var setupBuild = typeof(Program).Module.ModuleVersionId.ToString("N")[..8];
        Report($"[Setup] LabControl {setupVersion} build {setupBuild}");
        var readinessWarnings = new HashSet<string>(StringComparer.Ordinal) { "network.wol_unverified" };
        var nicSystem = new WindowsNicSettingsSystem();
        IReadOnlyList<NicIdentity> adapters = [];
        NicWakeSelection wake = new(NicWakeSelectionStatus.NoPhysicalEthernet, null);
        try
        {
            adapters = nicSystem.ListEthernetAdapters();
            wake = nicSystem.SelectWakeAdapter();
            foreach (var item in nicSystem.HardwareInventory())
                Report(string.Format(SetupDialog.Text("NicInventory"), item.Adapter.Name, item.Adapter.InterfaceId,
                    item.Adapter.PnpDeviceId, item.Adapter.MacAddress ?? SetupDialog.Text("Unknown"),
                    item.DriverProvider ?? SetupDialog.Text("Unknown"), item.DriverVersion ?? SetupDialog.Text("Unknown")));
        }
        catch (Exception error) when (error is IOException or NicSettingUnavailableException or UnauthorizedAccessException)
        {
            readinessWarnings.Add("network.configuration_warning");
            Report(SetupDialog.Text("WakeInventoryUnavailable"));
        }
        if (wake.Adapter is null) readinessWarnings.Add("network.configuration_warning");
        Report(wake.Adapter is { } target
            ? string.Format(SetupDialog.Text("WakeAdapter"), target.Name, target.MacAddress)
            : string.Format(SetupDialog.Text("WakeAdapterUnavailable"), wake.Status));
        var otherAntivirus = false;
        try
        {
            foreach (var product in WindowsAntivirusInventory.Read().Where(product => !product.IsMicrosoftDefender))
            {
                otherAntivirus = true;
                readinessWarnings.Add("antivirus.third_party");
                var warning = $"[Warning] Antivirus {product.Name}: exclude {Defaults.AgentInstallDirectory} in that product.";
                log.WriteLine(warning);
                Console.WriteLine(warning);
            }
        }
        catch (IOException)
        {
            readinessWarnings.Add("antivirus.inventory_unavailable");
            var warning = "[Warning] Antivirus inventory is unavailable. Review antivirus exclusions before relying on this installation.";
            log.WriteLine(warning);
            Console.WriteLine(warning);
        }
        var service = new WindowsSetupService(state.Read()!.InstallationId, files.Version);
        var serviceBytes = service.Desired;
        var uninstallEntry = new WindowsUninstallEntry(state.Read()!.InstallationId);
        state.BeginInstallDirectoryCreation(Directory.Exists(Defaults.AgentInstallDirectory));
        var steps = new List<ISetupStep>
        {
            new Step("Files", files.Check, files.Apply),
            new Step("Installed apps registration", () => scope.CheckSetting(uninstallEntry, _ => uninstallEntry.Desired), () => Require(scope.ApplySetting(uninstallEntry, uninstallEntry.Desired))),
            new OnceStep("Student account", () =>
            {
                scope.Prepare(existingInstallation: existing is not null, createStudent);
                scope.PrepareStudentMembership();
            }, selectedStudent),
            new Step("PC name", () => scope.CheckHostname(desiredName), () => Require(scope.ApplyHostname(desiredName))),
            new Step("Defender exclusion", () => scope.CheckDefenderExclusion(otherAntivirus), () => Require(scope.ApplyDefenderExclusion())),
            new Step("Enrollment", () => CheckEnrollment(payload, number), () =>
            {
                var mac = wake.Adapter?.MacAddress ?? MachineFacts.PrimaryMac() ?? throw new IOException("No network hardware address is available.");
                using var installed = AgentProvisioning.Install(Defaults.AgentDataDirectory, payload, payload.TakeCode(), number,
                    desiredName, mac, null, payload.Document.ConsolePort, MachineKeyProtection.Dpapi);
            }),
        };
        foreach (var policy in Enum.GetValues<SetupFirewallPolicy>())
            steps.Add(new Step("Firewall " + policy, () => scope.CheckFirewallRule(policy), () => Require(scope.ApplyFirewallRule(policy))));
        foreach (var policy in Enum.GetValues<MachineRegistryPolicy>())
            steps.Add(new Step("Machine policy " + policy, () => scope.CheckMachineRegistryPolicy(policy), () => Require(scope.ApplyMachineRegistryPolicy(policy))));
        steps.Add(new Step("Hibernation", scope.CheckHibernation, () => Require(scope.ApplyHibernation())));
        foreach (var adapter in adapters)
        foreach (var policy in NicSetting.ApplyOrder)
        {
            steps.Add(new Step("Network " + adapter.Name + " " + policy, () =>
            {
                try { return scope.CheckNicPolicy(adapter.InterfaceId, policy); }
                catch (NicSettingUnavailableException error) { return new(SetupStepStatus.Skipped, error.Message); }
            }, () => Require(scope.ApplyNicPolicy(adapter.InterfaceId, policy))));
        }
        foreach (var policy in Enum.GetValues<PowerPlanPolicy>())
            steps.Add(new Step("AC power " + policy, () => scope.CheckPowerPlanPolicy(policy), () => Require(scope.ApplyPowerPlanPolicy(policy)), refresh: true));
        steps.Add(new Step("Windows Update active hours", scope.CheckUpdateActiveHours, () => Require(scope.ApplyUpdateActiveHours())));
        if (templatePath is not null)
            steps.Add(new OnceStep("Default profile documents", () =>
            {
                foreach (var result in scope.ApplyProfileTemplate(templatePath)) Require(result);
            }));
        if (wake.Adapter?.MacAddress is { } wakeMac)
            steps.Add(new Step("Wake-on-LAN address", () =>
            {
                using var store = DirectoryAgentStore.Open(Defaults.AgentDataDirectory, MachineKeyProtection.Dpapi);
                return new(store.Config.Mac == wakeMac ? SetupStepStatus.AlreadyDone : SetupStepStatus.Needed);
            }, () =>
            {
                // Stop the writer before reopening configuration, then reload the physical MAC.
                _ = service.Read();
                var wasRunning = WindowsSetupService.IsRunning();
                WindowsSetupService.Stop();
                try
                {
                    using var store = DirectoryAgentStore.Open(Defaults.AgentDataDirectory, MachineKeyProtection.Dpapi);
                    store.Config.Mac = wakeMac;
                    store.SaveConfig();
                }
                finally
                {
                    if (wasRunning) WindowsSetupService.Start();
                }
            }));
        steps.Add(new OnceStep("Network readiness report", () => scope.SaveReadinessWarnings(readinessWarnings)));
        steps.Add(new Step("Service", () => scope.CheckSetting(service, _ => serviceBytes), () => Require(scope.ApplySetting(service, serviceBytes))));
        steps.Add(new OnceStep("Service recovery", WindowsSetupService.ConfigureRecovery));
        steps.Add(new Step("USB version upgrade", () => SetupVersionUpgrade.Check(files.Version), () => SetupVersionUpgrade.Apply(files.Version, state.Read()!.InstallationId)));
        steps.Add(new Step("Service start", () => new(WindowsSetupService.IsRunning() ? SetupStepStatus.AlreadyDone : SetupStepStatus.Needed), () =>
        {
            WindowsSetupService.Start();
        }));
        steps.Add(new OnceStep("Student automatic sign-in", () => Require(scope.ApplyStudentSignIn(desiredName)), selectedStudent));
        foreach (var policy in Enum.GetValues<StudentSessionPolicy>())
            steps.Add(new OnceStep("Session policy " + policy, () => Require(scope.ApplyStudentSessionPolicy(policy)), selectedStudent));
        steps.Add(new OnceStep("Verify installation", () =>
        {
            if (files.Check().Status != SetupStepStatus.AlreadyDone || CheckEnrollment(payload, number).Status != SetupStepStatus.AlreadyDone
                || InstallLayout.Default.ReadCurrent() != files.Version || !WindowsSetupService.IsRunning())
                throw new IOException("Installation verification failed.");
            _ = service.Read();
        }));
        steps.Add(new OnceStep("Student activation", () => scope.ActivateStudentAfterSuccessfulSetup(), selectedStudent));
        steps.Add(new OnceStep("Administrator sign-in visibility", () => Require(scope.HideSetupAdministrator()), selectedStudent));
        var results = SetupPipeline.Run(steps, false, result =>
        {
            if (result.Name.StartsWith("Network ", StringComparison.Ordinal) && result.Status == SetupStepStatus.Skipped)
                readinessWarnings.Add("network.configuration_warning");
            var line = $"[{result.Status}] {result.Name}{(result.Detail.Length > 0 ? ": " + result.Detail : "")}";
            log.WriteLine(line);
            Console.WriteLine(line);
        });
        if (results.Count == steps.Count && results.All(result => result.Status != SetupStepStatus.Conflict))
        {
            var incompleteWake = readinessWarnings.Contains("network.configuration_warning");
            Report(SetupDialog.Text(incompleteWake ? "WakeNeedsAttention" : "WakeWindowsConfigured"));
            Report(SetupDialog.Text("PushConfigured"));
        }
        return results.Count == steps.Count && results.All(result => result.Status != SetupStepStatus.Conflict) ? 0 : 1;
    }

    private static SetupCheck CheckEnrollment(SetupPayload payload, int number)
    {
        if (!DirectoryAgentStore.Exists(Defaults.AgentDataDirectory)) return new(SetupStepStatus.Needed);
        using var store = DirectoryAgentStore.Open(Defaults.AgentDataDirectory, MachineKeyProtection.Dpapi);
        return store.Config.Number == number && store.Config.LabId == payload.Document.LabId
            && store.Authority.RawData.AsSpan().SequenceEqual(payload.Authority.RawData)
            ? new(SetupStepStatus.AlreadyDone)
            : new(SetupStepStatus.Conflict, "Installed lab identity differs. Use the explicit rekey operation.");
    }

    private static void Preflight()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new InvalidOperationException("Windows 10 1809 or later is required.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Run Setup as an administrator.");
        var drive = new DriveInfo(Path.GetPathRoot(Defaults.AgentInstallDirectory)!);
        if (drive.AvailableFreeSpace < Defaults.SetupMinimumFreeBytes) throw new IOException("At least 2 GB of free disk space is required.");
    }

    private static void Require(SettingChangeResult? result)
    {
        if (result == SettingChangeResult.Conflict) throw new InvalidOperationException("A later or interrupted setting change was preserved.");
    }

    private sealed class Step(string name, Func<SetupCheck> check, Action apply, bool refresh = false) : ISetupStep
    {
        private bool _applied;
        public string Name => name;
        public SetupCheck Check()
        {
            var result = check();
            return refresh && !_applied && result.Status == SetupStepStatus.AlreadyDone
                ? new(SetupStepStatus.Needed, "Refresh the configured native policy.") : result;
        }
        public void Apply() { apply(); _applied = true; }
    }
    private sealed class OnceStep(string name, Action apply, bool enabled = true) : ISetupStep
    {
        private bool _done;
        public string Name => name;
        public SetupCheck Check() => new(!enabled ? SetupStepStatus.Skipped : _done ? SetupStepStatus.AlreadyDone : SetupStepStatus.Needed);
        public void Apply() { apply(); _done = true; }
    }
}

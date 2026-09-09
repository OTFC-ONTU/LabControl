using Xunit;

using LabControl.Shared;
using LabControl.Shared.Packaging;

namespace LabControl.Shared.Tests;

/// <summary>
/// The teacher-console packaging model (M5 portion 7, D-59): the installer's step plan,
/// the registry and shortcut data it writes, the firewall detection, and the command line.
/// All pure data, so the Windows installer's plan is asserted on the Mac; running it is a
/// separate Windows step.
/// </summary>
public class ConsolePackagingTests
{
    private static ConsoleInstallLayout Layout() => new(
        Path.Combine("C:", "Users", "teacher", "AppData", "Local"),
        Path.Combine("C:", "Users", "teacher", "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"),
        Path.Combine("C:", "Users", "teacher", "Desktop"),
        Path.Combine("C:", "Users", "teacher", "AppData", "Roaming", "LabControl"));

    private static ConsoleInstallPlan Plan(ConsoleInstallAction action, bool desktop = false) =>
        ConsoleInstallPlan.Build(
            new ConsoleInstallRequest { Action = action, DesktopShortcut = desktop },
            Layout(), "1.0.0", payloadFileCount: 3);

    // ------------------------------------------------------------------ the step plan

    [Fact]
    public void InstallPlanIsTheWholeInstallationInOrder()
    {
        var plan = Plan(ConsoleInstallAction.Install);

        Assert.Equal(
            [
                ConsoleInstallPlan.CheckLock,
                ConsoleInstallPlan.FilesReplace,
                ConsoleInstallPlan.ShortcutStartMenu,
                ConsoleInstallPlan.RegistryUninstall,
                ConsoleInstallPlan.ProgIdStep(ConsoleFileTypes.LabFile),
                ConsoleInstallPlan.ProgIdStep(ConsoleFileTypes.Backup),
                ConsoleInstallPlan.AssociationStep(ConsoleFileTypes.LabFile),
                ConsoleInstallPlan.AssociationStep(ConsoleFileTypes.Backup),
                ConsoleInstallPlan.ShellNotify,
                ConsoleInstallPlan.FirewallHint,
            ],
            plan.Steps.Select(step => step.Id));
    }

    [Fact]
    public void TheDesktopShortcutIsTheOnlyOptionalInstallStep()
    {
        var without = Plan(ConsoleInstallAction.Install).Steps.Select(step => step.Id).ToArray();
        var with = Plan(ConsoleInstallAction.Install, desktop: true).Steps.Select(step => step.Id).ToArray();

        Assert.DoesNotContain(ConsoleInstallPlan.ShortcutDesktop, without);
        Assert.Contains(ConsoleInstallPlan.ShortcutDesktop, with);
        Assert.Equal(without.Length + 1, with.Length);
        Assert.Equal(without, with.Where(id => id != ConsoleInstallPlan.ShortcutDesktop));
    }

    [Fact]
    public void InstallingNeverTouchesTheFirewallOrTheData()
    {
        var plan = Plan(ConsoleInstallAction.Install, desktop: true);

        Assert.DoesNotContain(plan.Steps, step => ConsoleFirewallRules.Required
            .Any(spec => step.Id == ConsoleInstallPlan.FirewallStep(spec)));
        Assert.DoesNotContain(plan.Steps, step => step.Id == ConsoleInstallPlan.DataRemove);
    }

    [Fact]
    public void UninstallRemovesOnlyOwnedThingsAndKeepsTheLabs()
    {
        var plan = Plan(ConsoleInstallAction.Uninstall);
        var ids = plan.Steps.Select(step => step.Id).ToArray();

        Assert.Equal(
            [
                ConsoleInstallPlan.CheckLock,
                ConsoleInstallPlan.AssociationRemoveStep(ConsoleFileTypes.LabFile),
                ConsoleInstallPlan.AssociationRemoveStep(ConsoleFileTypes.Backup),
                ConsoleInstallPlan.ProgIdRemoveStep(ConsoleFileTypes.LabFile),
                ConsoleInstallPlan.ProgIdRemoveStep(ConsoleFileTypes.Backup),
                ConsoleInstallPlan.RegistryUninstallRemove,
                ConsoleInstallPlan.ShortcutStartMenuRemove,
                ConsoleInstallPlan.ShortcutDesktopRemove,
                ConsoleInstallPlan.FirewallRemove,
                ConsoleInstallPlan.ShellNotify,
                ConsoleInstallPlan.FilesRemove,
                ConsoleInstallPlan.DataKeep,
            ],
            ids);

        // Removing the app never removes a lab: that is a separate, explicit switch.
        Assert.DoesNotContain(ConsoleInstallPlan.DataRemove, ids);
        var keep = plan.Steps.Single(step => step.Id == ConsoleInstallPlan.DataKeep);
        Assert.Contains(Layout().DataDirectory, keep.Text, StringComparison.Ordinal);
        Assert.Contains(Defaults.ConsoleSetupRemoveDataSwitch, keep.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingDataIsItsOwnPlanAndSaysWhatIsLost()
    {
        var plan = Plan(ConsoleInstallAction.RemoveData);

        Assert.Equal([ConsoleInstallPlan.CheckLock, ConsoleInstallPlan.DataRemove], plan.Steps.Select(step => step.Id));
        var remove = plan.Steps.Single(step => step.Id == ConsoleInstallPlan.DataRemove);
        Assert.Contains(Layout().DataDirectory, remove.Text, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", remove.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirewallPlanIsExactlyTheTwoScopedRules()
    {
        var plan = Plan(ConsoleInstallAction.Firewall);

        Assert.Equal(
            [
                ConsoleInstallPlan.FirewallStep(ConsoleFirewallRules.Control),
                ConsoleInstallPlan.FirewallStep(ConsoleFirewallRules.Discovery),
            ],
            plan.Steps.Select(step => step.Id));
        Assert.All(plan.Steps, step => Assert.Contains("Private and Domain", step.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void DryRunPrintsTheHeaderAndEveryStepWithItsRealPaths()
    {
        var layout = Layout();
        var printed = ConsoleInstallPlan
            .Build(new ConsoleInstallRequest { DryRun = true, DesktopShortcut = true }, layout, "1.0.0", 3)
            .Describe();

        var lines = printed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Defaults.ConsoleProductName + " 1.0.0 — install for the signed-in user", lines[0]);
        Assert.Equal(11, lines.Length - 1);
        Assert.All(lines.Skip(1), line => Assert.StartsWith("  ", line, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(layout.InstallDirectory, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(layout.StartMenuShortcut, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(layout.DesktopShortcut, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(layout.LockFile, StringComparison.Ordinal));
        // "3 file(s)" — the plan knows the payload before anything is written.
        Assert.Contains(lines, line => line.Contains("3 file(s)", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ what is written

    [Fact]
    public void EverythingIsUnderTheUsersOwnProfile()
    {
        var layout = Layout();

        Assert.StartsWith(layout.LocalAppData, layout.InstallDirectory, StringComparison.Ordinal);
        Assert.StartsWith(layout.LocalAppData, layout.LogFile, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("Programs", "LabControl", "Console"), layout.InstallDirectory, StringComparison.Ordinal);
        Assert.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\", ConsoleUninstallEntry.Key, StringComparison.Ordinal);
        Assert.All(ConsoleFileTypes.All, type => Assert.StartsWith(@"Software\Classes\", type.ProgIdKey, StringComparison.Ordinal));
    }

    [Fact]
    public void TheUninstallEntryPointsAtThisInstallation()
    {
        var layout = Layout();
        var values = ConsoleUninstallEntry.StringValues(layout, "1.0.0");

        Assert.Equal(Defaults.ConsoleProductName, values["DisplayName"]);
        Assert.Equal("1.0.0", values["DisplayVersion"]);
        Assert.Equal("LabControl", values["Publisher"]);
        Assert.Equal(layout.InstallDirectory, values["InstallLocation"]);
        Assert.Equal(layout.ConsoleExecutable + ",0", values["DisplayIcon"]);
        Assert.Equal("\"" + layout.SetupExecutable + "\" " + Defaults.ConsoleSetupUninstallSwitch, values["UninstallString"]);
        Assert.Equal(1, ConsoleUninstallEntry.DwordValues["NoModify"]);
        Assert.Equal(1, ConsoleUninstallEntry.DwordValues["NoRepair"]);
    }

    [Fact]
    public void BothFileTypesQuoteThePathAndTheArgument()
    {
        var layout = Layout();

        Assert.Equal(Defaults.ConsoleLabFileProgId, ConsoleFileTypes.LabFile.ProgId);
        Assert.Equal(Defaults.LabFileExtension, ConsoleFileTypes.LabFile.Extension);
        Assert.Equal(Defaults.ConsoleBackupProgId, ConsoleFileTypes.Backup.ProgId);
        Assert.Equal(Defaults.BackupFileExtension, ConsoleFileTypes.Backup.Extension);

        var command = ConsoleFileType.CommandFor(layout.ConsoleExecutable);
        Assert.Equal("\"" + layout.ConsoleExecutable + "\" \"%1\"", command);
        // A single quoted %1: a path with spaces must arrive as one argument.
        Assert.Single(command.Split("\"%1\""), part => part.Length > 0);

        foreach (var type in ConsoleFileTypes.All)
        {
            Assert.Equal(type.ProgIdKey + @"\shell\open\command", type.CommandKey);
            Assert.Equal(@"Software\Classes\" + type.Extension + @"\OpenWithProgids", type.OpenWithProgidsKey);
            Assert.EndsWith(type.Extension + @"\UserChoice", type.UserChoiceKey, StringComparison.Ordinal);
            Assert.Contains(@"Explorer\FileExts\", type.UserChoiceKey, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ firewall detection

    private static ConsoleFirewallRuleView Rule(ConsoleFirewallRuleSpec spec) => new()
    {
        Name = spec.Name,
        Grouping = ConsoleFirewallRules.Group,
        Protocol = spec.Protocol,
        LocalPorts = spec.LocalPorts,
        Direction = ConsoleFirewallRuleSpec.DirectionIn,
        Action = ConsoleFirewallRuleSpec.ActionAllow,
        Enabled = true,
        Profiles = ConsoleFirewallRuleSpec.Profiles,
    };

    [Fact]
    public void TheTwoRulesAreTheTwoPortsFromDefaults()
    {
        Assert.Equal(Defaults.ConsolePort, ConsoleFirewallRules.Control.Port);
        Assert.Equal(ConsoleFirewallRuleSpec.Tcp, ConsoleFirewallRules.Control.Protocol);
        Assert.Equal(Defaults.BeaconPort, ConsoleFirewallRules.Discovery.Port);
        Assert.Equal(ConsoleFirewallRuleSpec.Udp, ConsoleFirewallRules.Discovery.Protocol);
        Assert.Equal(Defaults.ConsoleProductName, ConsoleFirewallRules.Group);
        // Private | Domain, never Public.
        Assert.Equal(6, ConsoleFirewallRuleSpec.Profiles);
    }

    [Fact]
    public void NothingObservedMeansBothRulesAreMissing()
    {
        var status = ConsoleFirewallRules.Evaluate([]);

        Assert.False(status.Allowed);
        Assert.Equal(["LabControl Console (control)", "LabControl Console (discovery)"],
            status.Missing.Select(spec => spec.Name));
        Assert.Empty(status.OwnedRuleNames);
    }

    [Fact]
    public void BothOwnedRulesPresentMeansAllowed()
    {
        var status = ConsoleFirewallRules.Evaluate([Rule(ConsoleFirewallRules.Control), Rule(ConsoleFirewallRules.Discovery)]);

        Assert.True(status.Allowed);
        Assert.Empty(status.Missing);
        Assert.Equal(["LabControl Console (control)", "LabControl Console (discovery)"], status.OwnedRuleNames);
    }

    [Fact]
    public void SomebodyElsesRuleCountsButIsNotOwned()
    {
        var foreign = Rule(ConsoleFirewallRules.Control) with
        {
            Name = "Some other classroom tool",
            Grouping = "Some other tool",
            LocalPorts = "47000-48000",
        };

        var status = ConsoleFirewallRules.Evaluate([foreign, Rule(ConsoleFirewallRules.Discovery)]);

        Assert.True(status.Allowed);
        // Only this installer's rule may ever be removed on uninstall.
        Assert.Equal(["LabControl Console (discovery)"], status.OwnedRuleNames);
    }

    [Theory]
    [InlineData(false, true, 1, 1, 6, "the rule is disabled")]
    [InlineData(true, true, 2, 1, 6, "the rule is outbound")]
    [InlineData(true, true, 1, 0, 6, "the rule blocks")]
    [InlineData(true, true, 1, 1, 4, "only the Domain profile")]
    [InlineData(true, true, 1, 1, 2, "only the Private profile")]
    [InlineData(true, false, 1, 1, 6, "the wrong port")]
    public void ARuleThatDoesNotActuallyOpenThePortIsNoAnswer(
        bool enabled, bool rightPort, int direction, int action, int profiles, string because)
    {
        var rule = Rule(ConsoleFirewallRules.Control) with
        {
            Enabled = enabled,
            LocalPorts = rightPort ? ConsoleFirewallRules.Control.LocalPorts : "47999",
            Direction = direction,
            Action = action,
            Profiles = profiles,
        };

        var status = ConsoleFirewallRules.Evaluate([rule, Rule(ConsoleFirewallRules.Discovery)]);

        Assert.False(status.Allowed, because);
        Assert.Equal([ConsoleFirewallRules.Control.Name], status.Missing.Select(spec => spec.Name));
    }

    [Fact]
    public void ARuleOnEveryProfileCoversPrivateAndDomain()
    {
        var everywhere = Rule(ConsoleFirewallRules.Control) with { Profiles = ConsoleFirewallRuleSpec.AllProfiles };

        Assert.True(ConsoleFirewallRules.Evaluate([everywhere, Rule(ConsoleFirewallRules.Discovery)]).Allowed);
        Assert.True(ConsoleFirewallRules.CoversProfiles(7));
        Assert.False(ConsoleFirewallRules.CoversProfiles(1));
    }

    [Theory]
    [InlineData("47800", true)]
    [InlineData("*", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("47799-47801", true)]
    [InlineData("80,443,47800", true)]
    [InlineData(" 80 , 47800 ", true)]
    [InlineData("47801", false)]
    [InlineData("47801-47900", false)]
    [InlineData("RPC", false)]
    [InlineData("RPC,47800", true)]
    [InlineData("478000", false)]
    [InlineData("-47800", false)]
    public void ThePortListParserReadsWhatWindowsWrites(string? ports, bool covered) =>
        Assert.Equal(covered, ConsoleFirewallRules.CoversPort(ports, Defaults.ConsolePort));

    [Fact]
    public void TheDiagnosticIsTwoCompleteNetshLines()
    {
        var lines = ConsoleFirewallRules.NetshLines();

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.StartsWith("netsh advfirewall firewall add rule ", line, StringComparison.Ordinal);
            Assert.Contains("dir=in action=allow", line, StringComparison.Ordinal);
            Assert.Contains("profile=private,domain", line, StringComparison.Ordinal);
            Assert.Contains("group=\"" + ConsoleFirewallRules.Group + "\"", line, StringComparison.Ordinal);
        });
        Assert.Contains("protocol=TCP localport=47800", lines[0], StringComparison.Ordinal);
        Assert.Contains("protocol=UDP localport=47801", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeleteLinesRemoveExactlyTheTwoRulesTheAddLinesCreate()
    {
        var lines = ConsoleFirewallRules.NetshDeleteLines();

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.StartsWith("netsh advfirewall firewall delete rule ", line, StringComparison.Ordinal));
        foreach (var spec in ConsoleFirewallRules.Required)
        {
            Assert.Contains(lines, line => line.Contains("name=\"" + spec.Name + "\"", StringComparison.Ordinal));
        }
    }

    // ------------------------------------------- a rule that does not open the port for us

    [Fact]
    public void WhatTheInstallerCreatesIsExactlyWhatTheCheckAccepts()
    {
        var status = ConsoleFirewallRules.Evaluate(
            [.. ConsoleFirewallRules.Required.Select(ConsoleFirewallRules.AsCreated)]);

        Assert.True(status.Allowed);
        Assert.Equal(2, status.OwnedRuleNames.Count);
    }

    [Fact]
    public void ARuleScopedToAnotherProgramOpensNothingForThisConsole()
    {
        var conferencing = Rule(ConsoleFirewallRules.Control) with
        {
            Name = "Some conferencing tool",
            Grouping = "Some conferencing tool",
            ApplicationName = @"C:\Program Files\Conference\conference.exe",
        };

        var status = ConsoleFirewallRules.Evaluate([conferencing, Rule(ConsoleFirewallRules.Discovery)]);

        Assert.False(status.Allowed);
        Assert.Equal([ConsoleFirewallRules.Control.Name], status.Missing.Select(spec => spec.Name));
    }

    [Fact]
    public void AProgramScopedRuleCountsOnlyWhenItNamesThisVeryConsole()
    {
        const string installed = @"C:\Users\teacher\AppData\Local\LabControl\Console\" + Defaults.ConsoleExecutableName;
        const string elsewhere = @"D:\Portable\LabControl\" + Defaults.ConsoleExecutableName;
        var scoped = Rule(ConsoleFirewallRules.Control) with { ApplicationName = "\"" + installed + "\"" };

        // Told which console is asking, the rule must name that exact file.
        Assert.True(ConsoleFirewallRules.IsForThisConsole(scoped, installed));
        Assert.False(ConsoleFirewallRules.IsForThisConsole(scoped, elsewhere));

        // Told nothing, the file name is all an installer can honestly compare.
        Assert.True(ConsoleFirewallRules.IsForThisConsole(scoped, null));
        Assert.False(ConsoleFirewallRules.IsForThisConsole(
            scoped with { ApplicationName = @"C:\Windows\System32\svchost.exe" }, null));

        Assert.True(ConsoleFirewallRules.Evaluate(
            [scoped, Rule(ConsoleFirewallRules.Discovery)], installed).Allowed);
        Assert.False(ConsoleFirewallRules.Evaluate(
            [scoped, Rule(ConsoleFirewallRules.Discovery)], elsewhere).Allowed);
    }

    [Fact]
    public void ARuleScopedToAWindowsServiceIsNeverAnAnswer()
    {
        // The console is not a service, so a service rule that happens to cover the port
        // lets nothing of ours through.
        var service = Rule(ConsoleFirewallRules.Control) with { ServiceName = "RemoteRegistry" };

        Assert.False(ConsoleFirewallRules.IsForThisConsole(service, null));
        Assert.False(ConsoleFirewallRules.Evaluate([service, Rule(ConsoleFirewallRules.Discovery)]).Allowed);
    }

    [Theory]
    [InlineData("192.168.1.7", null, null, "one remote host")]
    [InlineData("192.168.1.0-192.168.1.50", null, null, "one remote range")]
    [InlineData(null, "10.0.0.4", null, "one local address")]
    [InlineData(null, null, "Wireless", "only Wi-Fi")]
    [InlineData(null, null, "Lan,Wireless", "a list, not All")]
    public void ARuleCutDownToOneCornerOfTheLabIsNoAnswer(
        string? remote, string? local, string? interfaces, string because)
    {
        var narrow = Rule(ConsoleFirewallRules.Control) with
        {
            RemoteAddresses = remote,
            LocalAddresses = local,
            InterfaceTypes = interfaces,
        };

        var status = ConsoleFirewallRules.Evaluate([narrow, Rule(ConsoleFirewallRules.Discovery)]);

        Assert.False(status.Allowed, because);
        Assert.Equal([ConsoleFirewallRules.Control.Name], status.Missing.Select(spec => spec.Name));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("*", true)]
    [InlineData("Any", true)]
    [InlineData("LocalSubnet", true)]
    [InlineData("localsubnet", true)]
    [InlineData("LocalSubnet,*", true)]
    [InlineData("LocalSubnet,192.168.1.7", false)]
    [InlineData("192.168.1.7", false)]
    public void TheAddressScopeParserReadsWhatWindowsWrites(string? addresses, bool reaches) =>
        Assert.Equal(reaches, ConsoleFirewallRules.ReachesTheClassroom(addresses));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("All", true)]
    [InlineData(" all ", true)]
    [InlineData("Lan", false)]
    [InlineData("Lan,Wireless,RemoteAccess", false)]
    public void OnlyAnUnrestrictedInterfaceListCoversATeacherWhoMoves(string? types, bool covers) =>
        Assert.Equal(covers, ConsoleFirewallRules.CoversEveryInterface(types));

    [Fact]
    public void OneMatchingBlockRuleOverridesEveryAllowRuleThereIs()
    {
        // Windows applies a matching block before any allow. Saying "allowed" here would send
        // the teacher looking at the student PCs for a problem that is on this machine.
        var block = Rule(ConsoleFirewallRules.Control) with
        {
            Name = "Block everything odd",
            Grouping = "Some policy",
            Action = ConsoleFirewallRuleSpec.ActionBlock,
            LocalPorts = "*",
        };

        var status = ConsoleFirewallRules.Evaluate(
            [ConsoleFirewallRules.AsCreated(ConsoleFirewallRules.Control), block, ConsoleFirewallRules.AsCreated(ConsoleFirewallRules.Discovery)]);

        Assert.False(status.Allowed);
        Assert.Equal([ConsoleFirewallRules.Control.Name], status.Missing.Select(spec => spec.Name));
        // The rule is still ours to remove on uninstall; it just does not open anything.
        Assert.Equal(2, status.OwnedRuleNames.Count);
    }

    [Fact]
    public void ABlockRuleIsJudgedGenerouslyButStillHasToConcernThisPort()
    {
        var narrowBlock = Rule(ConsoleFirewallRules.Control) with
        {
            Name = "Block one visitor",
            Grouping = "Some policy",
            Action = ConsoleFirewallRuleSpec.ActionBlock,
            // One profile, one address, one interface: still enough to stop the classroom.
            Profiles = 2,
            RemoteAddresses = "192.168.1.7",
            InterfaceTypes = "Wireless",
        };

        Assert.False(ConsoleFirewallRules.Evaluate(
            [ConsoleFirewallRules.AsCreated(ConsoleFirewallRules.Control), narrowBlock,
                ConsoleFirewallRules.AsCreated(ConsoleFirewallRules.Discovery)]).Allowed);

        // Another port, another program, the Public profile only, or disabled: not our problem.
        foreach (var harmless in new[]
                 {
                     narrowBlock with { LocalPorts = "47999" },
                     narrowBlock with { ApplicationName = @"C:\Games\game.exe" },
                     narrowBlock with { Profiles = 1 },
                     narrowBlock with { Enabled = false },
                     narrowBlock with { Direction = 2 },
                 })
        {
            Assert.True(ConsoleFirewallRules.Evaluate(
                [ConsoleFirewallRules.AsCreated(ConsoleFirewallRules.Control), harmless,
                    ConsoleFirewallRules.AsCreated(ConsoleFirewallRules.Discovery)]).Allowed);
        }
    }

    [Fact]
    public void ARuleIsOnlyOursToRemoveWhileEveryRuleOfThatNameIsOurs()
    {
        var spec = ConsoleFirewallRules.Control;
        var ours = ConsoleFirewallRules.AsCreated(spec);
        var namesake = ours with { Grouping = "Some other tool" };

        Assert.Equal(ConsoleFirewallRuleOwnership.Absent, ConsoleFirewallRules.Ownership([], spec));
        Assert.Equal(ConsoleFirewallRuleOwnership.Owned, ConsoleFirewallRules.Ownership([ours], spec));
        // Windows deletes by name, so one namesake makes the whole removal unsafe.
        Assert.Equal(ConsoleFirewallRuleOwnership.Foreign, ConsoleFirewallRules.Ownership([ours, namesake], spec));
        Assert.Equal(ConsoleFirewallRuleOwnership.Foreign, ConsoleFirewallRules.Ownership([namesake], spec));
    }

    // ------------------------------------------------------------------ the command line

    [Fact]
    public void NoArgumentsInstalls()
    {
        Assert.True(ConsoleInstallCommandLine.TryParse([], out var command, out _));

        Assert.Equal(ConsoleInstallAction.Install, command.Request.Action);
        Assert.False(command.Request.DesktopShortcut);
        Assert.False(command.Request.DryRun);
        Assert.False(command.Elevated);
    }

    [Theory]
    [InlineData("--uninstall", ConsoleInstallAction.Uninstall)]
    [InlineData("--remove-data", ConsoleInstallAction.RemoveData)]
    [InlineData("--firewall", ConsoleInstallAction.Firewall)]
    public void EachActionHasItsOwnSwitch(string argument, ConsoleInstallAction expected)
    {
        Assert.True(ConsoleInstallCommandLine.TryParse([argument, "--dry-run"], out var command, out _));

        Assert.Equal(expected, command.Request.Action);
        Assert.True(command.Request.DryRun);
    }

    [Fact]
    public void RemovingDataIsNeverImpliedByUninstalling()
    {
        Assert.False(ConsoleInstallCommandLine.TryParse(["--uninstall", "--remove-data"], out _, out var error));
        Assert.Contains("Choose one of", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDesktopIconOnlyMakesSenseWhenInstalling()
    {
        Assert.True(ConsoleInstallCommandLine.TryParse(["--desktop-shortcut"], out var install, out _));
        Assert.True(install.Request.DesktopShortcut);

        Assert.False(ConsoleInstallCommandLine.TryParse(["--uninstall", "--desktop-shortcut"], out _, out var error));
        Assert.Contains("only applies when installing", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownOptionIsRefusedRatherThanIgnored()
    {
        Assert.False(ConsoleInstallCommandLine.TryParse(["--all-users"], out _, out var error));
        Assert.Equal("Unknown option: --all-users", error);
    }

    [Fact]
    public void TheElevatedRelaunchCannotAskAgain()
    {
        Assert.True(ConsoleInstallCommandLine.TryParse(["--firewall", "--elevated"], out var command, out _));

        Assert.Equal(ConsoleInstallAction.Firewall, command.Request.Action);
        Assert.True(command.Elevated);
    }

    [Fact]
    public void TheTemporaryCopyIsToldWhichDirectoryToFinish()
    {
        Assert.True(ConsoleInstallCommandLine.TryParse(
            [ConsoleInstallCommandLine.FinishRemovalSwitch, @"C:\x\Console"], out var command, out _));
        Assert.Equal(@"C:\x\Console", command.FinishRemovalDirectory);

        Assert.False(ConsoleInstallCommandLine.TryParse([ConsoleInstallCommandLine.FinishRemovalSwitch], out _, out var error));
        Assert.Contains("needs the directory", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTemporaryCopyIsAlsoToldWhichProcessToWaitFor()
    {
        Assert.True(ConsoleInstallCommandLine.TryParse(
            [
                ConsoleInstallCommandLine.FinishRemovalSwitch, @"C:\x\Console",
                ConsoleInstallCommandLine.FinishRemovalProcessSwitch, "4321",
            ],
            out var command, out _));
        Assert.Equal(4321, command.FinishRemovalProcessId);

        // A process id on its own would delete nothing and wait for nobody.
        Assert.False(ConsoleInstallCommandLine.TryParse(
            [ConsoleInstallCommandLine.FinishRemovalProcessSwitch, "4321"], out _, out var alone));
        Assert.Contains("only applies together with", alone!, StringComparison.Ordinal);

        foreach (var bad in new[] { "", "0", "-1", "4321x", "99999999999999999999" })
        {
            Assert.False(ConsoleInstallCommandLine.TryParse(
                [
                    ConsoleInstallCommandLine.FinishRemovalSwitch, @"C:\x\Console",
                    ConsoleInstallCommandLine.FinishRemovalProcessSwitch, bad,
                ],
                out _, out var error), bad);
            Assert.Contains("needs the process id", error!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnlyAStepThatMerelyLookedMayClaimNothingWasChanged()
    {
        // A step that writes has predecessors that already ran, so "nothing was changed" would
        // be a lie that sends a teacher away from a half-finished installation.
        Assert.True(ConsoleInstallPlan.ChangesNothing(ConsoleInstallPlan.CheckLock));
        Assert.True(ConsoleInstallPlan.ChangesNothing(ConsoleInstallPlan.FirewallHint));
        Assert.True(ConsoleInstallPlan.ChangesNothing(ConsoleInstallPlan.DataKeep));

        foreach (var writing in new[]
                 {
                     ConsoleInstallPlan.FilesReplace, ConsoleInstallPlan.FilesRemove,
                     ConsoleInstallPlan.RegistryUninstall, ConsoleInstallPlan.RegistryUninstallRemove,
                     ConsoleInstallPlan.ShortcutStartMenu, ConsoleInstallPlan.FirewallRemove,
                     ConsoleInstallPlan.DataRemove,
                 })
        {
            Assert.False(ConsoleInstallPlan.ChangesNothing(writing), writing);
        }
    }

    // ------------------------------------------------------------------ the roots Windows gives

    [Fact]
    public void ALayoutWindowsWouldNotGiveIsRefusedRatherThanWrittenBlind()
    {
        // Not the Windows-shaped Layout() above: "C:\…" is not a rooted path on the Mac these
        // tests run on, and the rule under test is exactly "is this root a full path".
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "labcontrol-layout"));
        var usable = new ConsoleInstallLayout(root, root, root, root);
        Assert.Empty(usable.UnusableRoots());

        // GetFolderPath answers "" for a shell folder Windows has not materialised; every path
        // below it would then be relative to whatever directory the installer happened to be in.
        Assert.Equal(
            ["the local application data directory (Windows reported no path)"],
            (usable with { LocalAppData = "" }).UnusableRoots());

        var relativeDesktop = (usable with { Desktop = "Desktop" }).UnusableRoots();
        Assert.Single(relativeDesktop);
        Assert.Contains("is not a full path", relativeDesktop[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageNamesEverySwitchItAccepts()
    {
        var usage = ConsoleInstallCommandLine.Usage;

        foreach (var switchName in new[]
                 {
                     Defaults.ConsoleSetupUninstallSwitch, Defaults.ConsoleSetupRemoveDataSwitch,
                     Defaults.ConsoleSetupFirewallSwitch, Defaults.ConsoleSetupDesktopShortcutSwitch,
                     Defaults.ConsoleSetupDryRunSwitch,
                 })
        {
            Assert.Contains(switchName, usage, StringComparison.Ordinal);
        }
    }
}

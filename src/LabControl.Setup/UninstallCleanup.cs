using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace LabControl.Setup;

internal static class UninstallCleanup
{
    private static readonly string[] LastDataFiles = [Defaults.InstallationFileName, Defaults.InstallationFileName + ".tmp",
        Defaults.SetupSettingsFileName, Defaults.SetupSettingsFileName + ".tmp", Defaults.SetupLockFileName];

    public static void Start(string installationId)
    {
        var directory = WorkerDirectory(installationId);
        EnsurePrivateDirectory(Defaults.UninstallRecoveryRootDirectory);
        EnsurePrivateDirectory(directory);
        var worker = Path.Combine(directory, Defaults.UninstallExecutableName);
        var source = Environment.ProcessPath ?? throw new IOException("The running installer path is unavailable.");
        if (!File.Exists(worker))
        {
            var temporary = worker + ".tmp";
            SetupInstallationFiles.Guard(temporary);
            if (File.Exists(temporary)) { RequirePrivate(new FileInfo(temporary)); SetupInstallationFiles.ClearReadOnly(temporary); }
            File.Copy(source, temporary, true);
            RequirePrivate(new FileInfo(temporary));
            if (LabControl.Shared.Files.FileHash.Sha256HexOfFile(temporary) != LabControl.Shared.Files.FileHash.Sha256HexOfFile(source))
                throw new IOException("The cleanup executable changed during copying.");
            SetupInstallationFiles.ClearReadOnly(temporary);
            File.Move(temporary, worker, false);
        }
        RequirePrivate(new FileInfo(worker));
        if (LabControl.Shared.Files.FileHash.Sha256HexOfFile(worker) != LabControl.Shared.Files.FileHash.Sha256HexOfFile(source))
            throw new IOException("A different cleanup executable exists; preserve it for review.");
        // Keep Installed apps usable even if the original directory is partly removed.
        new WindowsUninstallEntry(installationId).PointToCleanup(worker);
        var info = new ProcessStartInfo(worker) { UseShellExecute = false, WorkingDirectory = directory };
        info.ArgumentList.Add(Defaults.FinishUninstallSwitch);
        info.ArgumentList.Add(installationId);
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(info) ?? throw new IOException("The removal cleanup process did not start.");
    }

    public static int Finish(string installationId, int parentPid)
    {
        var criticalRemoved = false;
        try
        {
            var directory = WorkerDirectory(installationId);
            var worker = Path.Combine(directory, Defaults.UninstallExecutableName);
            if (!string.Equals(Environment.ProcessPath, worker, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Final cleanup must run from its protected retry location.");
            RequirePrivate(new DirectoryInfo(Defaults.UninstallRecoveryRootDirectory));
            RequirePrivate(new DirectoryInfo(directory));
            RequirePrivate(new FileInfo(worker));
            if (parentPid > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    if (!parent.WaitForExit((int)Defaults.ServiceStopTimeout.TotalMilliseconds))
                        throw new IOException("The original installer did not close; removal can be retried.");
                }
                catch (ArgumentException) { }
            }
            using var mutex = SetupCoordinator.AcquireProcessLock();
            var receiptPath = Path.Combine(directory, Defaults.UninstallCleanupReceiptFileName);
            var hasReceipt = File.Exists(receiptPath);
            if (hasReceipt)
            {
                RequirePrivate(new FileInfo(receiptPath));
                var receipt = JsonStore.Load<CleanupReceipt>(receiptPath, CleanupReceipt.Migrations);
                if (receipt.InstallationId != installationId || !receipt.CriticalContentsRemoved)
                    throw new IOException("The cleanup completion receipt is invalid.");
            }
            if (WindowsSetupService.Exists()) throw new IOException("A service still exists; its files must be preserved.");
            // Check both trees before deleting either. Journals and the installation
            // marker remain until a receipt proves all non-metadata contents are gone.
            RejectTreeLinks(Defaults.AgentInstallDirectory);
            RejectTreeLinks(Defaults.AgentDataDirectory);
            if (Directory.Exists(Defaults.AgentDataDirectory))
            {
                using (var scope = AccountSetupScope.Open())
                {
                    var document = new InstallationState(Defaults.AgentDataDirectory).Read();
                    RemovalCleanupAuthorization.Require(installationId, document, hasReceipt, Directory.Exists(Defaults.AgentInstallDirectory));
                    if (!hasReceipt)
                    {
                        if (Directory.Exists(Defaults.AgentInstallDirectory))
                            SetupInstallationFiles.RequireOwnership(installationId);
                        DeleteContentsExcept(Defaults.AgentInstallDirectory, [Defaults.InstallDirectoryIdentityFileName]);
                        DeleteContentsExcept(Defaults.AgentDataDirectory, LastDataFiles);
                        SaveReceipt(receiptPath, installationId);
                        hasReceipt = true;
                    }
                    else
                    {
                        RequireOnly(Defaults.AgentInstallDirectory, [Defaults.InstallDirectoryIdentityFileName]);
                        RequireOnly(Defaults.AgentDataDirectory, LastDataFiles);
                    }
                    RemoveEmptyInstallationRoot(installationId);
                }
                // The global setup mutex remains held after releasing setup.lock.
                RequireOnly(Defaults.AgentDataDirectory, LastDataFiles);
                foreach (var name in LastDataFiles)
                {
                    var path = Path.Combine(Defaults.AgentDataDirectory, name);
                    if (File.Exists(path)) DeleteOwnedEntry(path);
                }
                SetupInstallationFiles.ClearReadOnly(Defaults.AgentDataDirectory);
                Directory.Delete(Defaults.AgentDataDirectory, false);
            }
            else
            {
                RemovalCleanupAuthorization.Require(installationId, null, hasReceipt);
                RequireOnly(Defaults.AgentInstallDirectory, [Defaults.InstallDirectoryIdentityFileName]);
                RemoveEmptyInstallationRoot(installationId);
            }
            criticalRemoved = true;
            new WindowsUninstallEntry(installationId).RemoveCleanupEntry(worker);
            // No installed service, files or protected journals remain. Only this mapped
            // executable and its nonsecret receipt need the explicitly reported reboot.
            foreach (var path in new[] { worker, receiptPath, directory })
            {
                SetupInstallationFiles.ClearReadOnly(path);
                if (!PInvoke.MoveFileEx(path, null, MOVE_FILE_FLAGS.MOVEFILE_DELAY_UNTIL_REBOOT))
                    throw new IOException("Temporary cleanup could not be scheduled.");
            }
            if (Directory.EnumerateFileSystemEntries(Defaults.UninstallRecoveryRootDirectory).All(path => path == directory))
                _ = PInvoke.MoveFileEx(Defaults.UninstallRecoveryRootDirectory, null, MOVE_FILE_FLAGS.MOVEFILE_DELAY_UNTIL_REBOOT);
            System.Console.WriteLine("LabControl was removed. Reboot to remove the protected temporary cleanup executable.");
            return 0;
        }
        catch (Exception)
        {
            System.Console.Error.WriteLine(criticalRemoved
                ? "The agent and its data were removed, but cleanup metadata remains. Reboot, then review the private LabControl Removal folder if it remains."
                : "Removal cleanup is incomplete. Retry LabControl in Installed apps; its protected cleanup executable and unresolved history were retained.");
            return 1;
        }
    }

    private static void RemoveEmptyInstallationRoot(string installationId)
    {
        if (!Directory.Exists(Defaults.AgentInstallDirectory)) return;
        RequireOnly(Defaults.AgentInstallDirectory, [Defaults.InstallDirectoryIdentityFileName]);
        var marker = Path.Combine(Defaults.AgentInstallDirectory, Defaults.InstallDirectoryIdentityFileName);
        if (File.Exists(marker))
        {
            SetupInstallationFiles.RequireOwnership(installationId);
            DeleteOwnedEntry(marker);
        }
        SetupInstallationFiles.ClearReadOnly(Defaults.AgentInstallDirectory);
        Directory.Delete(Defaults.AgentInstallDirectory, false);
    }

    private static void DeleteContentsExcept(string root, string[] preserved)
    {
        if (!Directory.Exists(root)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (preserved.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)) continue;
            DeleteOwnedEntry(entry);
        }
    }
    private static void DeleteOwnedEntry(string path)
    {
        SetupInstallationFiles.Guard(path);
        if (Directory.Exists(path))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteOwnedEntry(child);
            SetupInstallationFiles.ClearReadOnly(path);
            Directory.Delete(path, false);
        }
        else
        {
            SetupInstallationFiles.ClearReadOnly(path);
            File.Delete(path);
        }
    }
    private static void RequireOnly(string root, string[] allowed)
    {
        if (!Directory.Exists(root)) return;
        RejectTreeLinks(root);
        if (Directory.EnumerateFileSystemEntries(root).Any(path => !allowed.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
            throw new IOException("New files appeared after cleanup was authorized; preserve them for review.");
    }
    private static void SaveReceipt(string path, string installationId)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CleanupReceipt
        { InstallationId = installationId, CriticalContentsRemoved = true }, JsonStore.Options);
        var temporary = path + ".tmp";
        SetupInstallationFiles.Guard(temporary);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        File.Move(temporary, path, false);
    }
    private static string WorkerDirectory(string installationId)
    {
        if (!Guid.TryParseExact(installationId, "D", out var id) || id == Guid.Empty)
            throw new IOException("The cleanup identity is invalid.");
        return Path.Combine(Defaults.UninstallRecoveryRootDirectory, installationId);
    }
    private static void EnsurePrivateDirectory(string path)
    {
        SetupInstallationFiles.Guard(path);
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            security.SetAccessRuleProtection(true, false);
            foreach (var type in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        RequirePrivate(new DirectoryInfo(path));
    }
    private static void RequirePrivate(FileSystemInfo entry)
    {
        SetupInstallationFiles.Guard(entry.FullName);
        var security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : (FileSystemSecurity)((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!admin.Equals(owner) && !system.Equals(owner)) throw new IOException("Cleanup storage has an untrusted owner.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !admin.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))
                throw new IOException("Cleanup storage must be private to SYSTEM and Administrators.");
    }
    private static void RejectTreeLinks(string root)
    {
        SetupInstallationFiles.Guard(root);
        if (!Directory.Exists(root)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Removal found a redirected path; preserve it for review.");
            if ((attributes & FileAttributes.Directory) != 0) RejectTreeLinks(entry);
        }
    }
    private sealed class CleanupReceipt : ISchemaVersioned
    {
        public static readonly SchemaMigrations Migrations = new(1);
        public int SchemaVersion { get; set; } = 1;
        public string InstallationId { get; set; } = string.Empty;
        public bool CriticalContentsRemoved { get; set; }
    }
}

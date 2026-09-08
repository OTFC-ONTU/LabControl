using System.Security.AccessControl;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

internal sealed class SetupInstallationFiles(string payloadDirectory)
{
    public string InstallationId { private get; set; } = string.Empty;
    public string Version { get; } = FindVersion(payloadDirectory);
    private string Source => Path.Combine(payloadDirectory, Defaults.UsbBinariesDirectoryName, Defaults.AgentAppDirectoryName, Version);
    public string SourceAgentExecutable => Path.Combine(Source, Defaults.AgentExecutableName);
    private InstallLayout Layout => InstallLayout.Default;

    public void ValidateSource()
    {
        foreach (var source in UpdateBundle.RequiredFiles.Select(name => Path.Combine(Source, name))
                     .Append(Path.Combine(payloadDirectory, Defaults.SetupExecutableName)))
        {
            Guard(source);
            if (!File.Exists(source) || new FileInfo(source).Length == 0)
                throw new InvalidDataException("The USB agent or Setup build is incomplete.");
        }
    }

    public SetupCheck Check()
    {
        var needed = false;
        Guard(Layout.Root);
        VerifyInstalledPath(Layout.Root);
        if (Directory.Exists(Layout.Root)) RequireOwnership(InstallationId);
        foreach (var marker in new[] { Layout.CurrentFile, Layout.PreviousFile })
        {
            Guard(marker);
            VerifyInstalledPath(marker);
            Guard(marker + ".tmp");
            VerifyInstalledPath(marker + ".tmp");
        }
        var target = Layout.VersionDirectory(Version);
        foreach (var name in UpdateBundle.RequiredFiles)
        {
            var source = Path.Combine(Source, name);
            var destination = Path.Combine(target, name);
            Guard(source);
            Guard(destination);
            VerifyInstalledPath(destination);
            if (!File.Exists(source) || new FileInfo(source).Length == 0)
                return new(SetupStepStatus.Conflict, "The USB agent build is incomplete.");
            if (!File.Exists(destination)) { needed = true; continue; }
            if (!string.Equals(FileHash.Sha256HexOfFile(source), FileHash.Sha256HexOfFile(destination), StringComparison.Ordinal))
                return new(SetupStepStatus.Conflict, "Installed files differ from this USB build. Use a new build version; existing files were preserved.");
        }
        foreach (var name in new[] { Defaults.SetupExecutableName, Defaults.UninstallExecutableName })
        {
            var installed = Path.Combine(Layout.Root, name);
            Guard(installed);
            VerifyInstalledPath(installed);
            if (!File.Exists(installed)) { needed = true; continue; }
            if (FileHash.Sha256HexOfFile(installed) != FileHash.Sha256HexOfFile(Path.Combine(payloadDirectory, Defaults.SetupExecutableName)))
                needed = true;
        }
        return new(needed ? SetupStepStatus.Needed : SetupStepStatus.AlreadyDone);
    }

    public void Apply()
    {
        Guard(Layout.Root);
        VerifyInstalledPath(Layout.Root);
        var root = new DirectoryInfo(Layout.Root);
        if (!root.Exists)
        {
            if (!Guid.TryParseExact(InstallationId, "D", out _)) throw new IOException("Installation identity is unavailable.");
            CreateOwnedRoot(InstallationId);
        }
        RequireOwnership(InstallationId);
        RequireBinarySecurity(root);
        var destination = Layout.VersionDirectory(Version);
        Guard(destination);
        VerifyInstalledPath(destination);
        Directory.CreateDirectory(destination);
        VerifyInstalledPath(destination);
        foreach (var name in UpdateBundle.RequiredFiles)
            CopyMissing(Path.Combine(Source, name), Path.Combine(destination, name));
        var setup = Path.Combine(payloadDirectory, Defaults.SetupExecutableName);
        CopyMissing(setup, Path.Combine(Layout.Root, Defaults.SetupExecutableName), replaceInstaller: true);
        CopyMissing(setup, Path.Combine(Layout.Root, Defaults.UninstallExecutableName), replaceInstaller: true);
    }

    public static string StagingDirectory(string installationId)
    {
        if (!Guid.TryParseExact(installationId, "D", out _)) throw new IOException("Installation identity is unavailable.");
        return Defaults.AgentInstallDirectory + "." + installationId + Defaults.SetupInstallStagingSuffix;
    }

    public static void RemoveOwnedStaging(string installationId)
    {
        var path = StagingDirectory(installationId);
        Guard(path);
        if (!Directory.Exists(path))
        {
            if (File.Exists(path)) throw new IOException("The installation staging path is not a directory.");
            return;
        }
        RequireBinarySecurity(new DirectoryInfo(path));
        var marker = Path.Combine(path, Defaults.InstallDirectoryIdentityFileName);
        var temporary = marker + ".tmp";
        var entries = Directory.EnumerateFileSystemEntries(path).Take(3).ToArray();
        if (entries.Length > 2 || entries.Any(entry => !string.Equals(entry, marker, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry, temporary, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The installation staging directory contains unexpected files.");
        foreach (var entry in entries)
        {
            Guard(entry);
            if (!File.Exists(entry)) throw new IOException("An installation staging entry is not a regular file.");
            RequireBinarySecurity(new FileInfo(entry));
            if (new FileInfo(entry).Length > installationId.Length
                || string.Equals(entry, marker, StringComparison.OrdinalIgnoreCase) && File.ReadAllText(entry) != installationId)
                throw new IOException("The installation staging identity differs.");
        }
        foreach (var entry in entries) File.Delete(entry);
        Directory.Delete(path, recursive: false);
    }

    private static void CreateOwnedRoot(string installationId)
    {
        // The private creation intent already records this random installation ID. A
        // sibling on the same volume lets the final owned root appear atomically.
        var stagedPath = StagingDirectory(installationId);
        Guard(stagedPath);
        var staged = new DirectoryInfo(stagedPath);
        if (!staged.Exists) staged.Create(PublicBinarySecurity());
        RequireBinarySecurity(staged);
        var marker = Path.Combine(stagedPath, Defaults.InstallDirectoryIdentityFileName);
        var temporaryMarker = marker + ".tmp";
        var entries = Directory.EnumerateFileSystemEntries(stagedPath).Take(3).ToArray();
        if (entries.Length > 2 || entries.Any(path => !string.Equals(path, marker, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, temporaryMarker, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The installation staging directory contains unexpected files.");
        Guard(marker);
        Guard(temporaryMarker);
        if (File.Exists(temporaryMarker))
        {
            RequireBinarySecurity(new FileInfo(temporaryMarker));
            if (new FileInfo(temporaryMarker).Length > installationId.Length)
                throw new IOException("The staged identity temporary file is invalid.");
            File.Delete(temporaryMarker);
        }
        if (File.Exists(marker))
        {
            RequireBinarySecurity(new FileInfo(marker));
            if (new FileInfo(marker).Length != installationId.Length || File.ReadAllText(marker) != installationId)
                throw new IOException("The installation staging identity differs.");
        }
        else
        {
            // Only the exact installation-ID staging directory may resume empty.
            // No existing final directory is ever adopted.
            using (var identity = new FileStream(temporaryMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                identity.Write(System.Text.Encoding.UTF8.GetBytes(installationId));
                identity.Flush(flushToDisk: true);
            }
            File.Move(temporaryMarker, marker, overwrite: false);
        }
        RequireBinarySecurity(new FileInfo(marker));
        Guard(Defaults.AgentInstallDirectory);
        Directory.Move(stagedPath, Defaults.AgentInstallDirectory);
    }

    private static void CopyMissing(string source, string destination, bool replaceInstaller = false)
    {
        Guard(source);
        Guard(destination);
        VerifyInstalledPath(destination);
        if (File.Exists(destination))
        {
            if (FileHash.Sha256HexOfFile(source) == FileHash.Sha256HexOfFile(destination)) return;
            if (!replaceInstaller) throw new IOException("An existing installed executable differs; it was preserved.");
            if (string.Equals(Path.GetFullPath(destination), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Run Setup from the USB to replace this installed repair executable.");
        }
        var expected = FileHash.Sha256HexOfFile(source);
        var temporary = destination + ".tmp";
        Guard(temporary);
        VerifyInstalledPath(temporary);
        if (File.Exists(temporary)) ClearReadOnly(temporary);
        File.Copy(source, temporary, true);
        try
        {
            if (FileHash.Sha256HexOfFile(temporary) != expected) throw new IOException("A USB file changed during installation.");
            ClearReadOnly(temporary);
            if (replaceInstaller && File.Exists(destination)) ClearReadOnly(destination);
            File.Move(temporary, destination, replaceInstaller);
        }
        finally { if (File.Exists(temporary)) { ClearReadOnly(temporary); File.Delete(temporary); } }
    }

    // Call only after ownership/private-storage checks. Media attributes are not
    // ownership: a copied read-only executable must remain repairable/removable.
    internal static void ClearReadOnly(string path)
    {
        Guard(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    public static void ValidateInstalledVersion(string version)
    {
        foreach (var name in UpdateBundle.RequiredFiles)
        {
            var path = Path.Combine(InstallLayout.Default.VersionDirectory(version), name);
            Guard(path);
            VerifyInstalledPath(path);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException("The recorded agent version is incomplete.");
        }
    }

    public static void RequireOwnership(string installationId)
    {
        var marker = Path.Combine(Defaults.AgentInstallDirectory, Defaults.InstallDirectoryIdentityFileName);
        Guard(marker);
        VerifyInstalledPath(marker);
        if (!Guid.TryParseExact(installationId, "D", out _) || !File.Exists(marker) || new FileInfo(marker).Length != installationId.Length || File.ReadAllText(marker) != installationId)
            throw new IOException("The installed directory has no matching ownership identity; preserve it for review.");
    }

    public static void Guard(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Installer paths cannot contain reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string FindVersion(string directory)
    {
        var app = Path.Combine(directory, Defaults.UsbBinariesDirectoryName, Defaults.AgentAppDirectoryName);
        Guard(app);
        var directories = Directory.GetDirectories(app);
        if (directories.Length != 1 || !InstallLayout.IsValidVersion(Path.GetFileName(directories[0])))
            throw new InvalidDataException("The USB must contain exactly one agent version.");
        return Path.GetFileName(directories[0]);
    }

    private static DirectorySecurity PublicBinarySecurity()
    {
        var security = new DirectorySecurity();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(true, false);
        foreach (var type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.BuiltinUsersSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null),
                type == WellKnownSidType.BuiltinUsersSid ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void VerifyInstalledPath(string path)
    {
        var root = Path.GetFullPath(Defaults.AgentInstallDirectory);
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!current.Equals(root, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.Exists(current)) RequireBinarySecurity(new DirectoryInfo(current));
            else if (File.Exists(current)) RequireBinarySecurity(new FileInfo(current));
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static void RequireBinarySecurity(FileSystemInfo entry)
    {
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!admin.Equals(security.GetOwner(typeof(SecurityIdentifier))) && !system.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new IOException("The installed binary directory has an untrusted owner.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !admin.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference)
                && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                throw new IOException("The installed binary directory permits untrusted modification.");
        }
    }
}

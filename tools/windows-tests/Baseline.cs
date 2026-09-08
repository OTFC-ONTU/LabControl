using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LabControl.Setup;
using LabControl.Shared;
using LabControl.Shared.Files;
using Microsoft.Win32;

/// <summary>Test-only read-only evidence for the disposable VM. Never adopts its users.
/// Profile identity is measured, not mutable profile contents or login timestamps.</summary>
internal static class Baseline
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabControl.NativeSmoke.Baseline");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LabControl.NativeSmoke.Baseline/v1");
    private const string ProfileKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private static readonly SecurityIdentifier Admin = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    public static IReadOnlyList<ProbeResult> Capture() => Run(capture: true, Root);
    public static IReadOnlyList<ProbeResult> Verify() => Run(capture: false, Root);

    public static IReadOnlyList<ProbeResult> Capture(string fixtureId) => Run(true, FixtureRoot(fixtureId));
    public static IReadOnlyList<ProbeResult> Verify(string fixtureId) => Run(false, FixtureRoot(fixtureId));
    private static string FixtureRoot(string id) => Guid.TryParseExact(id, "D", out var guid)
        ? Root + "." + guid.ToString("D") : throw new ArgumentException("A fixture GUID is required.");

    private static IReadOnlyList<ProbeResult> Run(bool capture, string root)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            RequireAdministrator();
            PrepareDirectory(capture, root);
            var lockPath = Path.Combine(root, "baseline.lock");
            CheckFile(lockPath);
            using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(root, "baseline.json");
            CheckFile(path); CheckFile(path + ".tmp");
            if (capture && (File.Exists(path) || File.Exists(path + ".tmp"))) throw new IOException();
            byte[] key;
            Document? document = null;
            if (capture) key = RandomNumberGenerator.GetBytes(32);
            else
            {
                if (new FileInfo(path).Length > 16384) throw new InvalidDataException();
                document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? throw new InvalidDataException();
                if (document.Schema != 1 || document.ProtectedKey.Length == 0 || document.Fingerprints.Count != 3) throw new InvalidDataException();
                key = ProtectedData.Unprotect(document.ProtectedKey, Entropy, DataProtectionScope.LocalMachine);
                if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new CryptographicException(); }
            }
            try
            {
                var fingerprints = new Dictionary<string, byte[]>
                {
                    ["student-account-identity-flags"] = Fingerprint(key, "account", Account),
                    ["profile-identity-path-owner"] = Fingerprint(key, "profiles", Profiles),
                    ["winlogon-lsa-signin"] = Fingerprint(key, "signin", SignIn),
                };
                if (capture)
                {
                    document = new(1, ProtectedData.Protect(key, Entropy, DataProtectionScope.LocalMachine), fingerprints);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
                    using (var file = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(bytes); file.Flush(flushToDisk: true); }
                    File.Move(path + ".tmp", path, overwrite: false);
                    return [new("baseline-captured", "passed", watch.ElapsedMilliseconds, null)];
                }
                return fingerprints.Select(pair => new ProbeResult(pair.Key,
                    document!.Fingerprints.TryGetValue(pair.Key, out var expected)
                        && expected.Length == 32 && CryptographicOperations.FixedTimeEquals(pair.Value, expected) ? "passed" : "failed",
                    watch.ElapsedMilliseconds, null)).ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        catch (Exception error)
        {
            return [new(capture ? "baseline-capture" : "baseline-verify", "failed", watch.ElapsedMilliseconds,
                error.GetType().Name + ":" + error.HResult.ToString("X8"))];
        }
    }

    private static byte[] Fingerprint(byte[] key, string domain, Action<IncrementalHash> append)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        Text(hash, domain); append(hash); return hash.GetHashAndReset();
    }
    private static void Bytes(IncrementalHash hash, byte[]? value)
    {
        hash.AppendData(BitConverter.GetBytes(value?.Length ?? -1));
        if (value is not null) hash.AppendData(value);
    }
    private static void Text(IncrementalHash hash, string? value)
    {
        var bytes = value is null ? null : Encoding.UTF8.GetBytes(value);
        try { Bytes(hash, bytes); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void Account(IncrementalHash hash)
    {
        var status = NetUserGetInfo(null, Defaults.StudentAccountName, 23, out var buffer);
        try
        {
            if (status == 2221) { Text(hash, null); return; }
            if (status != 0 || buffer == IntPtr.Zero) throw new IOException();
            var info = Marshal.PtrToStructure<UserInfo23>(buffer);
            Text(hash, new SecurityIdentifier(info.Sid).Value);
            Text(hash, Marshal.PtrToStringUni(info.Name));
            hash.AppendData(BitConverter.GetBytes(info.Flags));
        }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
    }
    private static void Profiles(IncrementalHash hash)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = hive.OpenSubKey(ProfileKey) ?? throw new IOException();
        var names = root.GetSubKeyNames().Order(StringComparer.Ordinal).ToArray();
        hash.AppendData(BitConverter.GetBytes(names.Length));
        foreach (var name in names)
        {
            Text(hash, name);
            using var profile = root.OpenSubKey(name) ?? throw new IOException();
            var value = profile.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is not string original) throw new InvalidDataException();
            Text(hash, original);
            hash.AppendData(BitConverter.GetBytes((int)profile.GetValueKind("ProfileImagePath")));
            var path = Environment.ExpandEnvironmentVariables(original);
            var exists = Directory.Exists(path);
            hash.AppendData([exists ? (byte)1 : (byte)0]);
            if (exists)
            {
                HandoutDelivery.RejectReparseAncestors(path);
                Text(hash, new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value ?? throw new IOException());
            }
        }
    }
    private static void SignIn(IncrementalHash hash)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(Defaults.WinlogonRegistryKey) ?? throw new IOException();
        foreach (var name in new[] { Defaults.AutoAdminLogonValue, Defaults.AutoLogonUserValue, Defaults.AutoLogonDomainValue,
                     Defaults.AutoLogonPasswordValue, Defaults.AutoLogonCountValue, "ForceAutoLogon", "AutoLogonSID" })
        {
            Text(hash, name);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) { Bytes(hash, null); continue; }
            hash.AppendData(BitConverter.GetBytes((int)key.GetValueKind(name)));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType());
            try { Bytes(hash, bytes); }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (value is byte[] raw) CryptographicOperations.ZeroMemory(raw);
            }
        }
        // Explicit test-only readonly access; no production ownership is inferred.
        var secret = new WindowsStudentSignInSecretStore(RequireAdministrator).Read();
        try { Bytes(hash, secret); }
        finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
    }

    private static void RequireAdministrator()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
    }
    private static void PrepareDirectory(bool create, string root)
    {
        HandoutDelivery.RejectReparseAncestors(root);
        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            if (!create) throw new DirectoryNotFoundException();
            var security = new DirectorySecurity();
            security.SetOwner(Admin); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { Admin, System })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security);
        }
        CheckAcl(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }
    private static void CheckFile(string path)
    {
        HandoutDelivery.RejectReparseAncestors(path);
        if (Directory.Exists(path)) throw new IOException();
        if (File.Exists(path)) CheckAcl(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }
    private static void CheckAcl(FileSystemSecurity security)
    {
        static bool Trusted(IdentityReference? sid) => Admin.Equals(sid) || System.Equals(sid);
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType != AccessControlType.Allow || !Trusted(rule.IdentityReference)) throw new UnauthorizedAccessException();
    }
    private sealed record Document(int Schema, byte[] ProtectedKey, Dictionary<string, byte[]> Fingerprints);
    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo23 { public IntPtr Name, FullName, Comment; public uint Flags; public IntPtr Sid; }
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetUserGetInfo(string? server, string user, uint level, out IntPtr buffer);
    [DllImport("netapi32.dll")]
    private static extern uint NetApiBufferFree(IntPtr buffer);
}

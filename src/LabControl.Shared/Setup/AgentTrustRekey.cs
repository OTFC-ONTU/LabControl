using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

public enum TrustRekeyBoundary { Staged, CertificateRemoved, KeyWritten, AuthorityWritten, ConfigWritten, Committed }

/// <summary>Roll forward the four fixed, installer-owned trust files under Setup's
/// exclusive private-directory lock while the service is stopped. The durable encrypted
/// intent is the source of truth after interruption; the agent refuses to open mixed
/// trust until that intent is committed. Accounts and settings are never read or changed.</summary>
public sealed class AgentTrustRekey(string directory, KeyProtection keyProtection, KeyProtection journalProtection,
    Action<TrustRekeyBoundary>? boundary = null)
{
    private string JournalPath => Path.Combine(directory, Defaults.TrustRekeyFileName);
    private static readonly string[] Names = [Defaults.AgentCertificateFileName, Defaults.AgentKeyFileName,
        Defaults.CaCertificateFileName, Defaults.AgentConfigFileName];

    public bool HasPending => File.Exists(JournalPath);

    public void Replace(SetupPayload payload)
    {
        CheckFiles();
        if (HasPending)
        {
            Resume();
            return;
        }
        var replacementHost = ReplacementHost(payload.Document.ConsoleHost);
        var replacementPort = payload.Document.ConsolePort;
        if (replacementPort is < 1 or > 65535) throw new InvalidDataException("Invalid replacement console port.");
        var originals = Names.ToDictionary(name => name, Fingerprint);
        using var current = DirectoryAgentStore.Open(directory, keyProtection);
        // Rerunning an already completed, not-yet-enrolled operation must not consume
        // another code. A newly requested rekey of an enrolled PC still rotates its key.
        if (current.Config.LabId == payload.Document.LabId && current.Certificate is null
            && !string.IsNullOrWhiteSpace(current.Config.EnrollmentCode)
            && current.Authority.RawData.AsSpan().SequenceEqual(payload.Authority.RawData)
            && string.Equals(current.Config.ConsoleHost, replacementHost, StringComparison.OrdinalIgnoreCase)
            && current.Config.ConsolePort == replacementPort) return;

        var config = JsonStore.Parse<AgentConfigDocument>(JsonStore.Serialize(current.Config, AgentConfigDocument.Migrations),
            Defaults.AgentConfigFileName, AgentConfigDocument.Migrations);
        ValidateConfig(config);
        config.LabId = payload.Document.LabId;
        config.ConsoleHost = replacementHost;
        config.ConsolePort = replacementPort;
        config.EnrollmentCode = payload.TakeCode();
        config.LastInstanceId = null;
        config.Revocations = [];
        ValidateConfig(config);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rawKey = key.ExportPkcs8PrivateKey();
        byte[] protectedKey;
        try { protectedKey = keyProtection.Protect(rawKey); }
        finally { CryptographicOperations.ZeroMemory(rawKey); }
        var contents = new RekeyContents
        {
            Config = config, ProtectedKey = protectedKey,
            Authority = payload.Authority.Export(X509ContentType.Cert), Originals = originals,
        };
        foreach (var name in Names)
            if (Fingerprint(name) != originals[name]) throw new IOException("Trust files changed while rekey was being prepared.");
        Save(contents);
        boundary?.Invoke(TrustRekeyBoundary.Staged);
        Resume();
    }

    /// <summary>Resume without the USB; never consume another code for staged intent.</summary>
    public void Resume()
    {
        CheckFiles();
        var contents = Read();
        var desired = new Dictionary<string, byte[]?>
        {
            [Defaults.AgentCertificateFileName] = null,
            [Defaults.AgentKeyFileName] = contents.ProtectedKey,
            [Defaults.CaCertificateFileName] = contents.Authority,
            [Defaults.AgentConfigFileName] = JsonSerializer.SerializeToUtf8Bytes(contents.Config, JsonStore.Options),
        };
        try
        {
            // Refuse external edits before starting, then repeat immediately before each
            // replacement. Each file may be old or exactly staged after a power cut.
            foreach (var name in Names) RequireExpected(name, desired[name], contents);
            foreach (var name in Names)
            {
                RequireExpected(name, desired[name], contents);
                var path = Path.Combine(directory, name);
                var bytes = desired[name];
                if (bytes is null)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else WriteAtomic(path, bytes);
                boundary?.Invoke(name == Defaults.AgentCertificateFileName ? TrustRekeyBoundary.CertificateRemoved
                    : name == Defaults.AgentKeyFileName ? TrustRekeyBoundary.KeyWritten
                    : name == Defaults.CaCertificateFileName ? TrustRekeyBoundary.AuthorityWritten : TrustRekeyBoundary.ConfigWritten);
            }
            // Only staged hashes may remain at commit; no later file edit is adopted.
            foreach (var name in Names)
                if (Fingerprint(name) != Hash(desired[name])) throw new IOException("Trust files changed before rekey could commit.");
            File.Delete(JournalPath);
            boundary?.Invoke(TrustRekeyBoundary.Committed);
        }
        finally
        {
            if (desired[Defaults.AgentConfigFileName] is { } bytes) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void RequireExpected(string name, byte[]? desired, RekeyContents contents)
    {
        var observed = Fingerprint(name);
        if (observed != contents.Originals[name] && observed != Hash(desired))
            throw new IOException("A trust file changed outside the pending rekey. Preserve the transaction for review.");
    }

    private void Save(RekeyContents contents)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(contents, JsonStore.Options);
        byte[] encrypted;
        try { encrypted = journalProtection.Protect(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        var envelope = new RekeyEnvelope { Protected = encrypted };
        WriteAtomic(JournalPath, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonStore.Options));
    }

    private RekeyContents Read()
    {
        byte[]? clear = null;
        try
        {
            var envelope = JsonStore.Load<RekeyEnvelope>(JournalPath, RekeyEnvelope.Migrations);
            clear = journalProtection.Unprotect(envelope.Protected);
            var contents = JsonSerializer.Deserialize<RekeyContents>(clear, JsonStore.Options)
                ?? throw new InvalidDataException();
            if (contents.SchemaVersion != 1 || contents.Originals.Count != Names.Length
                || Names.Any(name => !contents.Originals.ContainsKey(name))
                || contents.Originals.Values.Any(hash => hash is not null && (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))))
                throw new InvalidDataException();
            ValidateConfig(contents.Config);
            if (string.IsNullOrWhiteSpace(contents.Config.EnrollmentCode)) throw new InvalidDataException();
            using var authority = X509CertificateLoader.LoadCertificate(contents.Authority);
            var rawKey = keyProtection.Unprotect(contents.ProtectedKey);
            try
            {
                using var key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(rawKey, out var read);
                if (read != rawKey.Length) throw new InvalidDataException();
            }
            finally { CryptographicOperations.ZeroMemory(rawKey); }
            return contents;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or CryptographicException or SchemaVersionException
            or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("The pending trust replacement could not be validated. Preserve it for review.");
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    private static string? ReplacementHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (host.Length > 253) throw new InvalidDataException("Invalid replacement console host.");
        if (System.Net.IPAddress.TryParse(host, out var address))
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw new InvalidDataException("Invalid replacement console host.");
        return host;
    }

    private static void ValidateConfig(AgentConfigDocument config)
    {
        if (config.SchemaVersion != Defaults.AgentConfigSchemaVersion
            || !Guid.TryParse(config.AgentId, out var agent) || agent == Guid.Empty
            || !Guid.TryParse(config.LabId, out var lab) || lab == Guid.Empty
            || config.Number < 1 || config.Number > Defaults.MaxStudentPcs)
            throw new InvalidDataException("The agent identity is invalid; trust replacement was refused.");
    }

    private string? Fingerprint(string name)
    {
        var path = Path.Combine(directory, name);
        try
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(file));
        }
        catch (FileNotFoundException) { return null; }
    }
    private static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));

    private void CheckFiles()
    {
        foreach (var name in Names.Append(Defaults.TrustRekeyFileName))
            foreach (var suffix in new[] { "", ".tmp" })
            {
                try
                {
                    if ((File.GetAttributes(Path.Combine(directory, name + suffix)) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new IOException("Trust replacement files must be regular files.");
                }
                catch (FileNotFoundException) { }
            }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class RekeyEnvelope : ISchemaVersioned
    {
        public static readonly SchemaMigrations Migrations = new(1);
        public int SchemaVersion { get; set; } = 1;
        public byte[] Protected { get; set; } = [];
    }
    private sealed class RekeyContents
    {
        public int SchemaVersion { get; set; } = 1;
        public AgentConfigDocument Config { get; set; } = new();
        public byte[] Authority { get; set; } = [];
        public byte[] ProtectedKey { get; set; } = [];
        public Dictionary<string, string?> Originals { get; set; } = [];
    }
}

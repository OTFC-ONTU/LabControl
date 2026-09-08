using System.Security.Cryptography;
using System.Text;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class AgentTrustRekeyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "labcontrol-rekey-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private string Data => Path.Combine(_root, "agent");
    private string Usb => Path.Combine(_root, "usb");
    private KeyProtection Protection => new(Protect, Unprotect);
    private AgentTrustRekey Rekey(Action<TrustRekeyBoundary>? boundary = null) => new(Data, Protection, Protection, boundary);

    [Fact]
    public void Rekey_changes_trust_and_key_preserving_pc_identity_and_all_unrelated_files()
    {
        using var payloadAuthority = Arrange(out var payload, out var identity, out var oldKey).Authority;
        var account = Path.Combine(Data, Defaults.InstallationFileName);
        var settings = Path.Combine(Data, Defaults.SetupSettingsFileName);
        File.WriteAllText(account, "account-history-must-stay");
        File.WriteAllText(settings, "settings-history-must-stay");
        Rekey().Replace(payload);
        using var changed = DirectoryAgentStore.Open(Data, Protection);
        Assert.Equal(identity, changed.Config.AgentId);
        Assert.Equal(7, changed.Config.Number);
        Assert.Equal("original-host", changed.Config.Hostname);
        Assert.Equal(payload.Document.ConsoleHost, changed.Config.ConsoleHost);
        Assert.Equal(payload.Document.ConsolePort, changed.Config.ConsolePort);
        Assert.Equal(payload.Document.LabId, changed.Config.LabId);
        Assert.Equal(payload.Authority.RawData, changed.Authority.RawData);
        Assert.NotEqual(oldKey, Convert.ToHexString(changed.Key.ExportSubjectPublicKeyInfo()));
        Assert.Null(changed.Certificate);
        Assert.Empty(changed.Config.Revocations);
        Assert.Null(changed.Config.LastInstanceId);
        Assert.Equal("new-code-1", changed.Config.EnrollmentCode);
        Assert.Equal("account-history-must-stay", File.ReadAllText(account));
        Assert.Equal("settings-history-must-stay", File.ReadAllText(settings));
        Assert.False(Rekey().HasPending);
        Assert.Single(payload.Document.EnrollmentCodes);
        Rekey().Replace(payload);
        Assert.Single(payload.Document.EnrollmentCodes);
    }

    [Fact]
    public void Replacement_endpoint_is_durable_across_interruption_and_pending_enrollment_reruns()
    {
        using var authority = Arrange(out var payload, out var identity, out _).Authority;
        payload.Document.ConsoleHost = "new-console";
        payload.Document.ConsolePort = 61234;
        Assert.Throws<IOException>(() => Rekey(boundary =>
        {
            if (boundary == TrustRekeyBoundary.Staged) throw new IOException("interrupted");
        }).Replace(payload));
        Rekey().Resume();
        using (var changed = DirectoryAgentStore.Open(Data, Protection))
        {
            Assert.Equal("new-console", changed.Config.ConsoleHost);
            Assert.Equal(61234, changed.Config.ConsolePort);
            Assert.Equal(identity, changed.Config.AgentId);
        }
        Rekey().Replace(payload);
        Assert.Single(payload.Document.EnrollmentCodes);
        // Same new CA, still awaiting enrollment: a later corrected route must not be ignored.
        payload.Document.ConsoleHost = null;
        payload.Document.ConsolePort = 61235;
        Rekey().Replace(payload);
        using var routed = DirectoryAgentStore.Open(Data, Protection);
        Assert.Null(routed.Config.ConsoleHost);
        Assert.Equal(61235, routed.Config.ConsolePort);
        Assert.Equal(identity, routed.Config.AgentId);
        Assert.Empty(payload.Document.EnrollmentCodes);
    }

    [Theory]
    [InlineData("https://other.example/path", 47800)]
    [InlineData("console:1234", 47800)]
    [InlineData("console", 0)]
    [InlineData("console", 65536)]
    public void Invalid_replacement_endpoint_is_refused_before_consuming_code_or_changing_trust(string host, int port)
    {
        using var authority = Arrange(out var payload, out _, out _).Authority;
        var before = File.ReadAllBytes(Path.Combine(Data, Defaults.AgentConfigFileName));
        payload.Document.ConsoleHost = host;
        payload.Document.ConsolePort = port;
        Assert.Throws<InvalidDataException>(() => Rekey().Replace(payload));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Data, Defaults.AgentConfigFileName)));
        Assert.Equal(2, payload.Document.EnrollmentCodes.Count);
        Assert.False(Rekey().HasPending);
    }

    [Theory]
    [InlineData(TrustRekeyBoundary.Staged)]
    [InlineData(TrustRekeyBoundary.CertificateRemoved)]
    [InlineData(TrustRekeyBoundary.KeyWritten)]
    [InlineData(TrustRekeyBoundary.AuthorityWritten)]
    [InlineData(TrustRekeyBoundary.ConfigWritten)]
    public void Every_interrupted_boundary_resumes_without_usb_or_another_code(TrustRekeyBoundary failAt)
    {
        using var payloadAuthority = Arrange(out var payload, out var identity, out _).Authority;
        Assert.Throws<IOException>(() => Rekey(boundary =>
        {
            if (boundary == failAt) throw new IOException("simulated interruption");
        }).Replace(payload));
        Assert.True(Rekey().HasPending);
        Assert.Throws<InvalidOperationException>(() => DirectoryAgentStore.Open(Data, Protection));
        Directory.Delete(Usb, recursive: true);
        Rekey().Resume();
        using var changed = DirectoryAgentStore.Open(Data, Protection);
        Assert.Equal(identity, changed.Config.AgentId);
        Assert.Equal("new-code-1", changed.Config.EnrollmentCode);
        Assert.Null(changed.Certificate);
        Assert.False(Rekey().HasPending);
    }

    [Fact]
    public void Changed_trust_file_is_not_overwritten_during_resume()
    {
        using var payloadAuthority = Arrange(out var payload, out _, out _).Authority;
        Assert.Throws<IOException>(() => Rekey(_ => throw new IOException()).Replace(payload));
        var path = Path.Combine(Data, Defaults.AgentConfigFileName);
        File.WriteAllText(path, "external edit");
        Assert.Throws<IOException>(() => Rekey().Resume());
        Assert.Equal("external edit", File.ReadAllText(path));
        Assert.True(Rekey().HasPending);
    }

    [Fact]
    public void Staged_transaction_contains_ciphertext_only_and_corruption_fails_closed()
    {
        using var payloadAuthority = Arrange(out var payload, out _, out _).Authority;
        Assert.Throws<IOException>(() => Rekey(_ => throw new IOException()).Replace(payload));
        var path = Path.Combine(Data, Defaults.TrustRekeyFileName);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("new-code-1", text);
        Assert.DoesNotContain("original-host", text);
        Assert.DoesNotContain("PRIVATE KEY", text);
        File.WriteAllText(path, "{\"schema_version\":1,\"protected\":\"AAAA\"}");
        var failure = Assert.Throws<InvalidDataException>(() => Rekey().Resume());
        Assert.DoesNotContain("new-code", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.True(Rekey().HasPending);
    }

    [Fact]
    public void Failed_protection_never_starts_mutating_installed_trust()
    {
        using var payloadAuthority = Arrange(out var payload, out _, out _).Authority;
        var original = File.ReadAllBytes(Path.Combine(Data, Defaults.AgentConfigFileName));
        var failedProtection = new KeyProtection(_ => throw new CryptographicException(), Unprotect);
        Assert.Throws<CryptographicException>(() => new AgentTrustRekey(Data, Protection, failedProtection).Replace(payload));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Data, Defaults.AgentConfigFileName)));
        Assert.False(Rekey().HasPending);
        using var originalStore = DirectoryAgentStore.Open(Data, Protection);
        Assert.NotNull(originalStore.Certificate);
    }

    private SetupPayload Arrange(out SetupPayload payload, out string identity, out string oldKey)
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Usb);
        using var originalLab = TestLab.Create();
        var config = new AgentConfigDocument { LabId = originalLab.LabId, AgentId = Guid.NewGuid().ToString("D"), Number = 7,
            Hostname = "original-host", Mac = "00:11:22:33:44:55", ConsoleHost = "original-console", ConsolePort = 54321,
            LastInstanceId = "old-instance", Revocations = [new() { Serial = "old-lab" }] };
        using (var original = DirectoryAgentStore.Install(Data, config, originalLab.Authority, Protection))
        {
            oldKey = Convert.ToHexString(original.Key.ExportSubjectPublicKeyInfo());
            // The store is only responsible for public certificate persistence here.
            original.InstallCertificate(original.Key, originalLab.Authority);
        }
        identity = config.AgentId;
        using var nextLab = TestLab.Create();
        JsonStore.Save(Path.Combine(Usb, Defaults.SetupFileName), new SetupPayloadDocument
        { LabId = nextLab.LabId, EnrollmentCodes = ["new-code-1", "new-code-2"] }, SetupPayloadDocument.Migrations);
        File.WriteAllBytes(Path.Combine(Usb, Defaults.CaCertificateFileName), nextLab.Authority.RawData);
        payload = SetupPayload.Open(Usb);
        return payload;
    }

    private byte[] Protect(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_secret, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. nonce, .. tag, .. ciphertext];
    }
    private byte[] Unprotect(byte[] bytes)
    {
        if (bytes.Length < 28) throw new CryptographicException();
        var plaintext = new byte[bytes.Length - 28];
        using var aes = new AesGcm(_secret, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
        return plaintext;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

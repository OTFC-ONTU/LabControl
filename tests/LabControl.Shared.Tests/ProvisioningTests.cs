using Xunit;

using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;

namespace LabControl.Shared.Tests;

/// <summary>
/// INSTALLER.md step 4 as shared code: what <c>agent.exe --install</c>, the M4 installer and
/// the simulator all do with a USB payload.
/// </summary>
public sealed class ProvisioningTests
{
    [Fact]
    public void Install_takes_a_code_pins_the_authority_and_writes_the_configuration()
    {
        using var lab = TestLab.Create();
        var stick = TempDirectory();
        var data = TempDirectory();
        try
        {
            WritePayload(lab, stick, codes: ["AAAA-1111", "BBBB-2222"]);
            var payload = SetupPayload.Open(stick);
            var code = payload.TakeCode();

            using var store = AgentProvisioning.Install(data, payload, code, number: 7, hostname: "PC-07", mac: "AA:BB:CC:DD:EE:07", consoleHost: null, consolePort: Defaults.ConsolePort);

            Assert.Equal("AAAA-1111", store.Config.EnrollmentCode);
            Assert.Equal(7, store.Config.Number);
            Assert.Equal(lab.LabId, store.Config.LabId);
            Assert.Equal("AA:BB:CC:DD:EE:07", store.Config.Mac);
            Assert.True(Guid.TryParse(store.Config.AgentId, out _));
            Assert.Null(store.Certificate);
            Assert.Equal(lab.Authority.Thumbprint, store.Authority.Thumbprint);

            Assert.True(File.Exists(Path.Combine(data, Defaults.AgentConfigFileName)));
            Assert.True(File.Exists(Path.Combine(data, Defaults.AgentKeyFileName)));
            Assert.True(File.Exists(Path.Combine(data, Defaults.CaCertificateFileName)));

            // The stick remembers the spent code (INSTALLER.md: "marking the code it used").
            var rewritten = JsonStore.Load<SetupPayloadDocument>(Path.Combine(stick, Defaults.SetupFileName), SetupPayloadDocument.Migrations);
            Assert.Equal(["BBBB-2222"], rewritten.EnrollmentCodes);
            Assert.Equal(["AAAA-1111"], rewritten.UsedEnrollmentCodes);

            // Reopening gives the same identity back.
            using var reopened = DirectoryAgentStore.Open(data);
            Assert.Equal(store.Config.AgentId, reopened.Config.AgentId);
            Assert.Equal(store.Key.ExportSubjectPublicKeyInfo(), reopened.Key.ExportSubjectPublicKeyInfo());
        }
        finally
        {
            Directory.Delete(stick, recursive: true);
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void A_pinned_console_on_the_command_line_beats_the_one_on_the_stick()
    {
        using var lab = TestLab.Create();
        var stick = TempDirectory();
        var data = TempDirectory();
        try
        {
            WritePayload(lab, stick, codes: ["AAAA-1111"], consoleHost: "10.0.0.5");
            var payload = SetupPayload.Open(stick);

            using var fromStick = AgentProvisioning.Install(Path.Combine(data, "a"), payload, "AAAA-1111", 1, "PC-01", "AA:BB:CC:DD:EE:01", null, Defaults.ConsolePort);
            Assert.Equal("10.0.0.5", fromStick.Config.ConsoleHost);

            using var fromArgs = AgentProvisioning.Install(Path.Combine(data, "b"), payload, "AAAA-1111", 2, "PC-02", "AA:BB:CC:DD:EE:02", "192.168.64.1", 47810);
            Assert.Equal("192.168.64.1", fromArgs.Config.ConsoleHost);
            Assert.Equal(47810, fromArgs.Config.ConsolePort);
        }
        finally
        {
            Directory.Delete(stick, recursive: true);
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void A_stick_with_no_codes_left_refuses()
    {
        using var lab = TestLab.Create();
        var stick = TempDirectory();
        try
        {
            WritePayload(lab, stick, codes: []);
            var payload = SetupPayload.Open(stick);
            Assert.Throws<InvalidOperationException>(() => payload.TakeCode());
        }
        finally
        {
            Directory.Delete(stick, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void A_number_outside_the_lab_is_refused_before_anything_is_written(int number)
    {
        using var lab = TestLab.Create();
        var stick = TempDirectory();
        var data = TempDirectory();
        try
        {
            WritePayload(lab, stick, codes: ["AAAA-1111"]);
            var payload = SetupPayload.Open(stick);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AgentProvisioning.Install(Path.Combine(data, "x"), payload, "AAAA-1111", number, "PC", "AA:BB:CC:DD:EE:FF", null, Defaults.ConsolePort));
            Assert.False(Directory.Exists(Path.Combine(data, "x")));
        }
        finally
        {
            Directory.Delete(stick, recursive: true);
            Directory.Delete(data, recursive: true);
        }
    }

    [Theory]
    [InlineData("PC-07", 7)]
    [InlineData("pc-30", 30)]
    [InlineData("PC-00", null)]
    [InlineData("PC-31", null)]
    [InlineData("DESKTOP-ABC123", null)]
    [InlineData("", null)]
    public void The_number_is_read_from_a_conventional_hostname(string hostname, int? expected) =>
        Assert.Equal(expected, AgentProvisioning.NumberFromHostname(hostname));

    [Fact]
    public void Mac_addresses_have_one_spelling() =>
        Assert.Equal("02:00:5E:00:00:0A", MachineFacts.FormatMac([0x02, 0x00, 0x5E, 0x00, 0x00, 0x0A]));

    private static void WritePayload(LabKey lab, string stick, List<string> codes, string? consoleHost = null)
    {
        var document = new SetupPayloadDocument
        {
            LabId = lab.LabId,
            LabName = "Test lab",
            ConsoleHost = consoleHost,
            EnrollmentCodes = codes,
            WrittenBy = "test",
            WrittenAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        JsonStore.Save(Path.Combine(stick, Defaults.SetupFileName), document, SetupPayloadDocument.Migrations);
        File.WriteAllBytes(Path.Combine(stick, Defaults.CaCertificateFileName), lab.Authority.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "labcontrol-provision-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}

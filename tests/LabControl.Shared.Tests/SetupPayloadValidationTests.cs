using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class SetupPayloadValidationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65536)]
    public void Invalid_console_port_is_refused_before_opening_authority(int port)
    {
        var root = Directory.CreateTempSubdirectory("labcontrol-payload-validation-");
        try
        {
            JsonStore.Save(Path.Combine(root.FullName, Defaults.SetupFileName),
                new SetupPayloadDocument { ConsolePort = port }, SetupPayloadDocument.Migrations);
            Assert.Throws<InvalidDataException>(() => SetupPayload.Open(root.FullName));
        }
        finally { root.Delete(recursive: true); }
    }
}

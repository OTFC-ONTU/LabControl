using System.Runtime.CompilerServices;

// The standalone Windows acceptance probe calls the real adapters. It is not shipped
// in the USB payload and exposes no additional Setup command or production API.
[assembly: InternalsVisibleTo("LabControl.Setup.NativeSmoke")]

[assembly: InternalsVisibleTo("LabControl.UpdateRecoveryDrill")]

[assembly: InternalsVisibleTo("LabControl.InteractiveUninstall")]

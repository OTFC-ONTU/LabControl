using LabControl.Shared.Setup;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: UsbBuild <USB mount or payload directory> <publish directory> [version]");
    return 2;
}
try
{
    var version = args.Length == 3 ? args[2] : typeof(UsbInstallerBuilder).Assembly.GetName().Version!.ToString(3);
    var builder = new UsbInstallerBuilder(args[1], version);
    var destination = builder.Build(args[0]);
    Console.WriteLine($"USB installer {builder.Version} ready at {destination}");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine($"USB installer could not be built: {ex.Message}");
    return 1;
}

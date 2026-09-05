using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

/// <summary>
/// The USB payload directory as Setup.exe, <c>agent.exe --install</c> and the simulator see it
/// (INSTALLER.md): <c>setup.json</c> and <c>ca.crt</c>. A code taken by an install is moved
/// to the stick's used list, so two runs never hand out the same code.
/// </summary>
public sealed class SetupPayload
{
    private readonly string _path;

    private SetupPayload(string path, SetupPayloadDocument document, X509Certificate2 authority)
    {
        _path = path;
        Document = document;
        Authority = authority;
    }

    public SetupPayloadDocument Document { get; }

    public X509Certificate2 Authority { get; }

    public static SetupPayload Open(string directory)
    {
        var payloadDirectory = Directory.Exists(Path.Combine(directory, Defaults.PayloadDirectoryName))
            ? Path.Combine(directory, Defaults.PayloadDirectoryName)
            : directory;

        var path = Path.Combine(payloadDirectory, Defaults.SetupFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"'{path}' not found. Write a USB payload from the console (Settings → Enrol PCs → Write USB payload) and point --payload at it.");
        }

        var document = JsonStore.Load<SetupPayloadDocument>(path, SetupPayloadDocument.Migrations);
        var authority = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(payloadDirectory, Defaults.CaCertificateFileName));
        return new SetupPayload(path, document, authority);
    }

    public string TakeCode()
    {
        if (Document.EnrollmentCodes.Count == 0)
        {
            throw new InvalidOperationException(
                "The payload has no unused enrollment codes left; write a new one from the console.");
        }

        var code = Document.EnrollmentCodes[0];
        Document.EnrollmentCodes.RemoveAt(0);
        Document.UsedEnrollmentCodes.Add(code);
        JsonStore.Save(_path, Document, SetupPayloadDocument.Migrations);
        return code;
    }
}

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using LabControl.Shared.Identity;

namespace LabControl.Shared.Link;

/// <summary>
/// A gRPC channel from a PC to the console (ARCHITECTURE §3.4). The console is trusted
/// only if its leaf chains to the pinned lab authority, names this lab, is a console
/// certificate and is not revoked — never by hostname, IP or a public root (D-24). The
/// PC offers its own certificate when it has one; during enrolment it has none yet.
/// </summary>
public sealed class ConsoleChannel : IDisposable
{
    private readonly LabTrust _trust;
    private readonly RevocationSet? _revocations;
    private readonly X509Certificate2? _clientCertificate;

    private ConsoleChannel(string host, int port, LabTrust trust, RevocationSet? revocations, X509Certificate2? clientCertificate)
    {
        _trust = trust;
        _revocations = revocations;
        _clientCertificate = clientCertificate;

        var handler = new SocketsHttpHandler
        {
            // Dead-link detection at the transport level: a pulled cable is noticed within
            // the heartbeat timeout even when no application message was due.
            KeepAlivePingDelay = Defaults.HeartbeatInterval,
            KeepAlivePingTimeout = Defaults.HeartbeatTimeout,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            EnableMultipleHttp2Connections = false,
            ConnectTimeout = Defaults.HeartbeatTimeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = ValidateConsole,
            },
        };

        if (clientCertificate is not null)
        {
            handler.SslOptions.ClientCertificates = [clientCertificate];
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate;
        }

        Channel = GrpcChannel.ForAddress($"https://{FormatHost(host)}:{port}", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
    }

    public GrpcChannel Channel { get; }

    /// <summary>Why the console's certificate was refused, in plain language; <c>null</c> if it was accepted.</summary>
    public string? Refusal { get; private set; }

    public static ConsoleChannel Open(
        string host,
        int port,
        X509Certificate2 authority,
        string labId,
        X509Certificate2? clientCertificate = null,
        RevocationSet? revocations = null) =>
        new(host, port, new LabTrust(authority, labId), revocations,
            clientCertificate is null ? null : TlsCertificate.ForTls(clientCertificate));

    private bool ValidateConsole(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var leaf = certificate as X509Certificate2 ?? (certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));

        if (_trust.TryValidate(leaf, LabRole.Console, _revocations, out var name, out var failure))
        {
            Refusal = null;
            return true;
        }

        Refusal = $"the console's certificate was refused: {LabTrust.Describe(failure, name)}";
        return false;
    }

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    public void Dispose()
    {
        Channel.Dispose();
        _clientCertificate?.Dispose();
    }
}

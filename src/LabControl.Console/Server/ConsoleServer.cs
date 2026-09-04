using System.Security.Authentication;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Server;

/// <summary>
/// The gRPC server embedded in the console (ARCHITECTURE §2): Kestrel on
/// <see cref="ConsoleOptions.Port"/>, HTTP/2 over TLS, serving with the console instance's
/// leaf certificate and asking every client for one. The TLS layer accepts any client
/// certificate; whether it is <i>this lab's agent, not revoked</i> is decided per call by
/// <see cref="LabSession"/> through <see cref="LabTrust"/>, so a refusal comes back as a
/// plain-language gRPC status instead of a handshake error (D-24).
/// </summary>
public sealed class ConsoleServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ConsoleServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public static async Task<ConsoleServer> StartAsync(LabSession session, ILoggerFactory loggers)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggers);
        builder.Services.AddSingleton(session);
        builder.Services.AddGrpc();

        var serverCertificate = TlsCertificate.ForTls(session.Instance.Certificate);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.Http2.KeepAlivePingDelay = Defaults.HeartbeatInterval;
            kestrel.Limits.Http2.KeepAlivePingTimeout = Defaults.HeartbeatTimeout;

            kestrel.Listen(session.Options.BindAddress, session.Options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = serverCertificate;
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    https.ClientCertificateValidation = (_, _, _) => true;
                    https.CheckCertificateRevocation = false;
                });
            });
        });

        var app = builder.Build();
        app.MapGrpcService<EnrollmentGrpcService>();
        app.MapGrpcService<AgentGrpcService>();

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var port = session.Options.Port;
        var bound = addresses?.Addresses.FirstOrDefault();
        if (bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out var uri))
        {
            port = uri.Port;
        }

        return new ConsoleServer(app, port);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        await _app.DisposeAsync();
    }
}

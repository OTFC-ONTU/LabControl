using System.Net;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using LabControl.Console.Services;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Server;

/// <summary><c>EnrollmentService</c> (PROTOCOL): server-authenticated TLS only; the PC has no certificate yet.</summary>
public sealed class EnrollmentGrpcService : EnrollmentService.EnrollmentServiceBase
{
    private readonly LabSession _session;

    public EnrollmentGrpcService(LabSession session) => _session = session;

    public override Task<EnrollResponse> Enroll(EnrollRequest request, ServerCallContext context)
    {
        try
        {
            return Task.FromResult(_session.Enroll(request, RemoteAddress(context)));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            _session.Events.Error("enroll.failed", $"Enrolment of PC-{request.Number:00} failed inside the console: {ex.Message}", request.AgentId, request.Number);
            throw new RpcException(new Status(StatusCode.Internal, $"the console failed while enrolling: {ex.Message}"));
        }
    }

    internal static IPAddress RemoteAddress(ServerCallContext context) =>
        context.GetHttpContext().Connection.RemoteIpAddress?.MapToIPv4() ?? IPAddress.None;
}

/// <summary><c>AgentService</c> (PROTOCOL): mutual TLS, the peer certificate validated per call by the session.</summary>
public sealed class AgentGrpcService : AgentService.AgentServiceBase
{
    private readonly LabSession _session;

    public AgentGrpcService(LabSession session) => _session = session;

    public override Task Link(IAsyncStreamReader<AgentMessage> requestStream, IServerStreamWriter<ConsoleMessage> responseStream, ServerCallContext context) =>
        _session.LinkAsync(
            context.GetHttpContext().Connection.ClientCertificate,
            EnrollmentGrpcService.RemoteAddress(context),
            requestStream,
            responseStream,
            context.CancellationToken,
            () => context.GetHttpContext().Abort());

    public override Task<RenewResponse> Renew(RenewRequest request, ServerCallContext context) =>
        Task.FromResult(_session.Renew(context.GetHttpContext().Connection.ClientCertificate, request));

    // Video and files arrive in M3 and M4; until then the base class answers Unimplemented,
    // which an agent of any version treats as "not available on this console".
}

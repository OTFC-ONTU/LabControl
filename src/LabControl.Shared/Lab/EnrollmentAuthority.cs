using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Lab;

/// <summary>Why an <c>Enroll</c> call did or did not produce a certificate.</summary>
public enum EnrollmentOutcome
{
    Issued = 0,

    /// <summary>The console is not accepting enrolments right now (the lab key is locked).</summary>
    Closed = 1,

    /// <summary>The code is not one this console ever wrote to a USB stick.</summary>
    UnknownCode = 2,

    /// <summary>The code has already enrolled a PC. Reported as an event (D-14).</summary>
    BurnedCode = 3,

    /// <summary>The PC thinks it belongs to a different lab.</summary>
    WrongLab = 4,

    /// <summary>The PC number is outside 1..<see cref="Defaults.MaxStudentPcs"/> (D-17).</summary>
    BadNumber = 5,

    /// <summary>The signing request is not a valid PKCS#10, or is not signed by its own key.</summary>
    BadRequest = 6,
}

/// <summary>The result of one enrolment attempt, ready to be turned into a response or an event.</summary>
public sealed record EnrollmentResult(EnrollmentOutcome Outcome, X509Certificate2? Certificate, string Message)
{
    public bool Ok => Outcome == EnrollmentOutcome.Issued;
}

/// <summary>
/// Issues agent certificates against single-use codes (D-14, ARCHITECTURE §3.5). The USB
/// stick carries no secret: the worst a found stick allows is enrolling a bogus PC, which
/// appears in the console as an unexpected machine and is removed with one click.
/// <para>
/// Issuing needs the lab key, so the console holds it unlocked only while the teacher has
/// enrolment open — see D-24.
/// </para>
/// </summary>
public sealed class EnrollmentAuthority
{
    private readonly EnrollmentDocument _document;
    private readonly Lock _gate = new();

    public EnrollmentAuthority(EnrollmentDocument document) => _document = document;

    /// <summary>The document as it stands; the caller persists it after every change.</summary>
    public EnrollmentDocument Document => _document;

    public int UnusedCodeCount
    {
        get
        {
            lock (_gate)
            {
                return _document.Codes.Count(code => !code.IsBurned);
            }
        }
    }

    /// <summary>
    /// Writes a batch of codes for one USB stick. They are single use and worth nothing on
    /// their own, so there is no reason to be frugal: one per PC plus spares.
    /// </summary>
    public IReadOnlyList<string> Generate(int count, string batch, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var printable = new List<string>(count);
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                var code = EnrollmentCode.Generate();
                if (!EnrollmentCode.TryCanonicalize(code, out var canonical))
                {
                    throw new InvalidOperationException("A generated enrollment code did not canonicalize.");
                }

                _document.Codes.Add(new EnrollmentCodeRecord
                {
                    Code = canonical,
                    Batch = batch,
                    CreatedAtUnix = now.ToUnixTimeSeconds(),
                });

                printable.Add(code);
            }
        }

        return printable;
    }

    /// <summary>
    /// Redeems a code and issues the agent's certificate. The code is burned in the same
    /// step, so two PCs racing on the same code produce exactly one certificate.
    /// </summary>
    public EnrollmentResult Redeem(LabKey? lab, EnrollRequest request, DateTimeOffset now)
    {
        if (lab is null)
        {
            return new EnrollmentResult(EnrollmentOutcome.Closed, null,
                "This console is not accepting enrolments — open Settings and unlock the lab key.");
        }

        if (!string.Equals(request.LabId, lab.LabId, StringComparison.OrdinalIgnoreCase))
        {
            return new EnrollmentResult(EnrollmentOutcome.WrongLab, null,
                $"{Describe(request)} belongs to lab {request.LabId}, not this one.");
        }

        // The agent id ends up inside the certificate's name; an empty or malformed one would
        // mint a certificate that this lab's own trust rules then reject on every connection.
        if (!Guid.TryParseExact(request.AgentId, "d", out _))
        {
            return new EnrollmentResult(EnrollmentOutcome.BadRequest, null,
                $"{Describe(request)} presented an agent id that is not a UUID.");
        }

        if (request.Number is < 1 || request.Number > Defaults.MaxStudentPcs)
        {
            return new EnrollmentResult(EnrollmentOutcome.BadNumber, null,
                $"{Describe(request)} asked for number {request.Number}; a lab holds 1..{Defaults.MaxStudentPcs} PCs (D-17).");
        }

        if (!EnrollmentCode.TryCanonicalize(request.EnrollmentCode, out var canonical))
        {
            return new EnrollmentResult(EnrollmentOutcome.UnknownCode, null,
                $"{Describe(request)} presented a code that is not a LabControl enrollment code.");
        }

        EnrollmentCodeRecord record;
        lock (_gate)
        {
            var match = _document.Codes.FirstOrDefault(c => string.Equals(c.Code, canonical, StringComparison.Ordinal));
            if (match is null)
            {
                return new EnrollmentResult(EnrollmentOutcome.UnknownCode, null,
                    $"{Describe(request)} presented an enrollment code this lab never issued.");
            }

            if (match.IsBurned)
            {
                return new EnrollmentResult(EnrollmentOutcome.BurnedCode, null,
                    $"{Describe(request)} presented an enrollment code already used by " +
                    $"{string.Format(Defaults.MachineNameFormat, match.UsedByNumber)}.");
            }

            // Burn first: a certificate issued against a code that was not burned would let
            // the same stick enrol twice if issuance threw halfway through.
            match.UsedAtUnix = now.ToUnixTimeSeconds();
            match.UsedByAgentId = request.AgentId;
            match.UsedByNumber = request.Number;
            record = match;
        }

        try
        {
            var certificate = LabCertificates.IssueAgentFromCsr(
                lab.Authority, lab.LabId, request.AgentId, request.Number, request.Csr.ToByteArray(), now);

            return new EnrollmentResult(EnrollmentOutcome.Issued, certificate,
                $"{Describe(request)} enrolled.");
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            lock (_gate)
            {
                // The request was unusable, so the code was never really spent: give it back.
                record.UsedAtUnix = 0;
                record.UsedByAgentId = null;
                record.UsedByNumber = 0;
            }

            return new EnrollmentResult(EnrollmentOutcome.BadRequest, null,
                $"{Describe(request)} sent a signing request this console could not read: {ex.Message}");
        }
    }

    private static string Describe(EnrollRequest request) =>
        string.Format(Defaults.MachineNameFormat, request.Number) +
        (request.Hostname.Length > 0 ? $" ({request.Hostname})" : string.Empty);
}

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

    /// <summary>The code is from an older USB stick, voided when a newer payload was written (D-28).</summary>
    VoidedCode = 7,

    /// <summary>The code came in an imported backup and has not been activated on this console (M5, D-60).</summary>
    DormantCode = 8,
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

    /// <summary>Runs <paramref name="persist"/> with the code list held still (see <c>LabRegistry.Persist</c>).</summary>
    public void Persist(Action<EnrollmentDocument> persist)
    {
        lock (_gate)
        {
            persist(_document);
        }
    }

    public int UnusedCodeCount
    {
        get
        {
            lock (_gate)
            {
                return _document.Codes.Count(code => code.IsUsable);
            }
        }
    }

    /// <summary>Codes that arrived in a backup and wait for <i>Use codes from the imported backup</i> (D-60).</summary>
    public int DormantCodeCount
    {
        get
        {
            lock (_gate)
            {
                return _document.Codes.Count(code => code.IsOutstanding && code.IsDormant);
            }
        }
    }

    /// <summary>The batches with dormant codes, for the activation button's text (D-60).</summary>
    public IReadOnlyList<EnrollmentBatchRecord> DormantBatches
    {
        get
        {
            lock (_gate)
            {
                var names = _document.Codes.Where(code => code.IsOutstanding && code.IsDormant).Select(code => code.Batch).ToHashSet(StringComparer.Ordinal);
                var known = _document.Batches.Where(batch => names.Contains(batch.Batch)).ToList();
                foreach (var name in names.Where(name => known.All(batch => !string.Equals(batch.Batch, name, StringComparison.Ordinal))))
                {
                    known.Add(new EnrollmentBatchRecord { Batch = name });
                }

                return known;
            }
        }
    }

    /// <summary>
    /// Marks every outstanding code dormant: called when a backup lands in a saved lab
    /// (D-60 item 1), never by the single-lab migration (item 2). Returns how many.
    /// </summary>
    public int MarkDormant(DateTimeOffset now)
    {
        lock (_gate)
        {
            var marked = 0;
            foreach (var code in _document.Codes)
            {
                if (code.IsOutstanding && !code.IsDormant)
                {
                    code.DormantSinceImportUnix = now.ToUnixTimeSeconds();
                    marked++;
                }
            }

            foreach (var batch in _document.Batches)
            {
                if (!batch.IsDormant)
                {
                    batch.DormantSinceImportUnix = now.ToUnixTimeSeconds();
                }
            }

            return marked;
        }
    }

    /// <summary>The explicit administrator action of D-60: every dormant code is usable from now on. Returns how many.</summary>
    public int ActivateDormant()
    {
        lock (_gate)
        {
            var activated = 0;
            foreach (var code in _document.Codes)
            {
                if (code.IsOutstanding && code.IsDormant)
                {
                    code.DormantSinceImportUnix = 0;
                    activated++;
                }
            }

            foreach (var batch in _document.Batches)
            {
                batch.DormantSinceImportUnix = 0;
            }

            return activated;
        }
    }

    /// <summary>
    /// Voids every code that is still outstanding — dormant ones too: a new stick replaces
    /// every older one (D-28). Called before a new stick is written, so that a stick left in a
    /// drawer or lost cannot enrol anything once a newer one exists. Returns how many codes were voided.
    /// </summary>
    public int Supersede(DateTimeOffset now)
    {
        lock (_gate)
        {
            var voided = 0;
            foreach (var code in _document.Codes)
            {
                if (code.IsOutstanding)
                {
                    code.VoidedAtUnix = now.ToUnixTimeSeconds();
                    voided++;
                }
            }

            return voided;
        }
    }

    /// <summary>
    /// Writes a batch of codes for one USB stick. They are single use and worth nothing on
    /// their own, so there is no reason to be frugal: one per PC plus spares.
    /// </summary>
    public IReadOnlyList<string> Generate(int count, string batch, DateTimeOffset now, string? issuedByInstanceId = null, string? issuedByInstanceName = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var printable = new List<string>(count);
        lock (_gate)
        {
            _document.Batches.Add(new EnrollmentBatchRecord
            {
                Batch = batch,
                IssuedByInstanceId = issuedByInstanceId,
                IssuedByInstanceName = issuedByInstanceName,
                CreatedAtUnix = now.ToUnixTimeSeconds(),
            });

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
                    IssuedByInstanceId = issuedByInstanceId,
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
    public EnrollmentResult Redeem(LabKey? lab, EnrollRequest request, DateTimeOffset now, TimeSpan? lifetime = null)
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
                    $"{Describe(request)} presented an enrollment code this console never issued. If the stick was written on " +
                    "another teacher machine, enrol there, or export its backup and import it here — the codes travel with it.");
            }

            if (match.IsVoided)
            {
                return new EnrollmentResult(EnrollmentOutcome.VoidedCode, null,
                    $"{Describe(request)} presented a code from an older USB stick ({match.Batch}); it was voided on " +
                    $"{DateTimeOffset.FromUnixTimeSeconds(match.VoidedAtUnix):yyyy-MM-dd} when a newer payload was written. Install from the current stick.");
            }

            if (match.IsDormant)
            {
                return new EnrollmentResult(EnrollmentOutcome.DormantCode, null,
                    $"{Describe(request)} presented a code from a backup imported on this console ({match.Batch}); imported codes are dormant " +
                    "until the administrator chooses Settings → Enrollment → Use codes from the imported backup.");
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
                lab.Authority, lab.LabId, request.AgentId, request.Number, request.Csr.ToByteArray(), now, lifetime);

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

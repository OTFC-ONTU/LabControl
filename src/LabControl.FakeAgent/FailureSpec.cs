using System.Globalization;

namespace LabControl.FakeAgent;

/// <summary>What a simulated PC does wrong, on purpose (ROADMAP M1, "failure injection").</summary>
public enum FailureKind
{
    None = 0,

    /// <summary>Installed but never dials: the PC that is switched off.</summary>
    NeverConnects,

    /// <summary>Dials only after a delay: the PC that is still booting.</summary>
    ConnectsLate,

    /// <summary>Drops the link the moment a job arrives and reconnects later: tests idempotency.</summary>
    DiesMidJob,

    /// <summary>Every job fails with an error result.</summary>
    JobError,

    /// <summary>Enrols with a code another PC already used: refused, and the console shows an event.</summary>
    BurnedCode,

    /// <summary>Sends a revocation entry the lab key never signed: the console must ignore it.</summary>
    ForgedRevocation,

    /// <summary>Claims an older protocol version: the console keeps it, marked outdated.</summary>
    Outdated,
}

/// <summary>
/// One <c>--fail</c> argument: <c>7:never</c>, <c>8:late=20</c>, <c>9:die-mid-job</c>,
/// <c>10:job-error</c>, <c>11:burned-code</c>, <c>12:forged-revocation</c>, <c>13:outdated</c>.
/// </summary>
public sealed record FailureSpec(int Number, FailureKind Kind, int Seconds)
{
    public static readonly string Help =
        "N:never | N:late=SECONDS | N:die-mid-job | N:job-error | N:burned-code | N:forged-revocation | N:outdated";

    public static bool TryParse(string text, out FailureSpec spec, out string error)
    {
        spec = null!;
        error = string.Empty;

        var colon = text.IndexOf(':');
        if (colon <= 0 || !int.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            error = $"'{text}' must start with a PC number and a colon";
            return false;
        }

        var rest = text[(colon + 1)..];
        var equals = rest.IndexOf('=');
        var name = equals < 0 ? rest : rest[..equals];
        var seconds = 0;
        if (equals >= 0 && !int.TryParse(rest.AsSpan(equals + 1), NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
        {
            error = $"'{rest}' needs a number of seconds after '='";
            return false;
        }

        FailureKind kind;
        switch (name)
        {
            case "never": kind = FailureKind.NeverConnects; break;
            case "late": kind = FailureKind.ConnectsLate; seconds = seconds == 0 ? 20 : seconds; break;
            case "die-mid-job": kind = FailureKind.DiesMidJob; break;
            case "job-error": kind = FailureKind.JobError; break;
            case "burned-code": kind = FailureKind.BurnedCode; break;
            case "forged-revocation": kind = FailureKind.ForgedRevocation; break;
            case "outdated": kind = FailureKind.Outdated; break;
            default:
                error = $"'{name}' is not a failure this simulator knows ({Help})";
                return false;
        }

        spec = new FailureSpec(number, kind, seconds);
        return true;
    }
}

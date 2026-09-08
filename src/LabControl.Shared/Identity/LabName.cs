using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LabControl.Shared.Identity;

/// <summary>What a console leaf may do, read off its subject OU (M5, D-56 item 5).</summary>
public enum ConsoleAccess
{
    /// <summary>Not a console leaf, or a leaf whose OU this build does not know.</summary>
    Unknown = 0,

    /// <summary><c>OU=LabControl Console</c>: holds the lab key; may enrol, renew, revoke, sign updates.</summary>
    Administrator = 1,

    /// <summary><c>OU=LabControl Teacher</c>: drives the room; <c>self_update</c> and <c>rekey</c> are refused.</summary>
    Teacher = 2,
}

/// <summary>
/// Who a certificate says it is. Neither side of a LabControl connection trusts a
/// hostname, an IP address or a public CA (ARCHITECTURE §3.4), so identity is carried in
/// a subject alternative name URI that both sides parse:
/// <c>labcontrol://&lt;lab_id&gt;/console/&lt;instance_id&gt;</c> or
/// <c>labcontrol://&lt;lab_id&gt;/agent/&lt;agent_id&gt;/&lt;number&gt;</c>.
/// </summary>
public sealed record LabName(LabRole Role, string LabId, string Id, int Number)
{
    public const string UriScheme = "labcontrol";

    /// <summary>
    /// The access level of a console leaf, from its subject OU (D-56 item 5); <see cref="ConsoleAccess.Unknown"/>
    /// for every other role and for a name that did not come out of a certificate.
    /// </summary>
    public ConsoleAccess Access { get; init; }

    public static LabName ForAuthority(string labId) => new(LabRole.Authority, labId, labId, 0);

    public static LabName ForConsole(string labId, string instanceId) =>
        new(LabRole.Console, labId, instanceId, 0);

    public static LabName ForAgent(string labId, string agentId, int number) =>
        new(LabRole.Agent, labId, agentId, number);

    public Uri ToUri()
    {
        var path = Role switch
        {
            LabRole.Authority => "authority",
            LabRole.Console => $"console/{Id}",
            LabRole.Agent => $"agent/{Id}/{Number.ToString(CultureInfo.InvariantCulture)}",
            _ => throw new InvalidOperationException($"Cannot name role {Role}."),
        };

        return new Uri($"{UriScheme}://{LabId}/{path}");
    }

    /// <summary>Reads the identity back out of an issued certificate.</summary>
    public static bool TryFromCertificate(X509Certificate2 certificate, out LabName name)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != SubjectAlternativeNameOid)
            {
                continue;
            }

            foreach (var uri in EnumerateUris(extension.RawData))
            {
                if (TryParse(uri, out name))
                {
                    if (name.Role == LabRole.Console)
                    {
                        name = name with { Access = AccessOf(certificate) };
                    }

                    return true;
                }
            }
        }

        name = null!;
        return false;
    }

    private const string SubjectAlternativeNameOid = "2.5.29.17";
    private const string OrganizationalUnitOid = "2.5.4.11";

    /// <summary>
    /// The console access a leaf's subject OU claims; the chain check is the caller's job.
    /// Exactly one single-valued OU that equals the known string byte for byte, or
    /// <see cref="ConsoleAccess.Unknown"/>: two OUs, a multi-valued RDN, a trailing space or
    /// a subject this build cannot read all mean "no authority", never an exception — this
    /// runs inside TLS validation, before the chain is even checked.
    /// </summary>
    public static ConsoleAccess AccessOf(X509Certificate2 certificate)
    {
        string? unit = null;
        try
        {
            foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
            {
                if (rdn.HasMultipleElements)
                {
                    // An OU hidden inside a multi-valued RDN is not the OU the lab key writes.
                    return ConsoleAccess.Unknown;
                }

                if (rdn.GetSingleElementType().Value != OrganizationalUnitOid)
                {
                    continue;
                }

                if (unit is not null)
                {
                    return ConsoleAccess.Unknown;
                }

                unit = rdn.GetSingleElementValue();
            }
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or InvalidOperationException or ArgumentException)
        {
            return ConsoleAccess.Unknown;
        }

        if (string.Equals(unit, Defaults.ConsoleOrganizationalUnit, StringComparison.Ordinal))
        {
            return ConsoleAccess.Administrator;
        }

        if (string.Equals(unit, Defaults.TeacherOrganizationalUnit, StringComparison.Ordinal))
        {
            return ConsoleAccess.Teacher;
        }

        return ConsoleAccess.Unknown;
    }

    /// <summary>
    /// The BCL exposes DNS names and IP addresses from a SAN but not URIs, so the
    /// <c>uniformResourceIdentifier [6] IA5String</c> entries are read out of the DER here.
    /// </summary>
    private static IEnumerable<string> EnumerateUris(byte[] subjectAlternativeName)
    {
        AsnReader names;
        try
        {
            names = new AsnReader(subjectAlternativeName, AsnEncodingRules.DER).ReadSequence();
        }
        catch (AsnContentException)
        {
            yield break;
        }

        while (names.HasData)
        {
            string? uri = null;
            try
            {
                var tag = names.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                {
                    uri = names.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                }
                else
                {
                    names.ReadEncodedValue();
                }
            }
            catch (AsnContentException)
            {
                yield break;
            }

            if (uri is not null)
            {
                yield return uri;
            }
        }
    }

    public static bool TryParse(string? text, out LabName name)
    {
        name = null!;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, UriScheme, StringComparison.Ordinal))
        {
            return false;
        }

        var labId = uri.Host;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (labId.Length == 0 || parts.Length == 0)
        {
            return false;
        }

        switch (parts[0])
        {
            case "authority" when parts.Length == 1:
                name = ForAuthority(labId);
                return true;

            case "console" when parts.Length == 2 && parts[1].Length > 0:
                name = ForConsole(labId, parts[1]);
                return true;

            case "agent" when parts.Length == 3 && parts[1].Length > 0 &&
                              int.TryParse(parts[2], CultureInfo.InvariantCulture, out var number):
                name = ForAgent(labId, parts[1], number);
                return true;

            default:
                return false;
        }
    }
}

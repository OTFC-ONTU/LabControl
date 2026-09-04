using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;

namespace LabControl.Shared.Identity;

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
                    return true;
                }
            }
        }

        name = null!;
        return false;
    }

    private const string SubjectAlternativeNameOid = "2.5.29.17";

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

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LabControl.Shared.Identity;

namespace LabControl.Shared.Discovery;

/// <summary>Why a beacon was dropped. Everything but <see cref="None"/> costs nothing but a parse.</summary>
public enum BeaconFailure
{
    None = 0,
    Malformed = 1,
    WrongVersion = 2,
    WrongLab = 3,
    BadEndorsement = 4,
    BadSignature = 5,
    StaleTimestamp = 6,
}

/// <summary>
/// The UDP discovery beacon (PROTOCOL, "Discovery beacon"). It says only <i>where</i> to
/// look; mutual TLS is the real gate. Its point is that it is verifiable <b>offline with
/// no shared secret</b>: the CA endorses the instance's public key once, and the instance
/// signs every beacon, so a new teacher machine is followed by every PC without anyone
/// touching a PC (D-13), and nothing on a student PC can forge one.
/// </summary>
public sealed class Beacon
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    [JsonPropertyName("v")]
    public int Version { get; set; } = Defaults.BeaconVersion;

    [JsonPropertyName("lab")]
    public string LabId { get; set; } = string.Empty;

    [JsonPropertyName("inst")]
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The instance's display name, so another console can name it in the banner (§3.7.2).</summary>
    [JsonPropertyName("name")]
    public string InstanceName { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = Defaults.ConsolePort;

    [JsonPropertyName("ts")]
    public long SentAtUnix { get; set; }

    /// <summary>
    /// The moment <i>Take over the lab</i> was pressed, repeated for
    /// <see cref="Defaults.TakeOverWindow"/>; 0 when the console is not taking over. Each
    /// value is honoured once by an agent, so rebroadcasting it does not loop (§3.7.2).
    /// </summary>
    [JsonPropertyName("take")]
    public long TakeAtUnix { get; set; }

    /// <summary>The console instance's public key, P-256 compressed, 33 bytes.</summary>
    [JsonPropertyName("pub")]
    public byte[] PublicKey { get; set; } = [];

    /// <summary>The lab key's signature over <c>lab|inst|base64(pub)</c>, 64 bytes.</summary>
    [JsonPropertyName("end")]
    public byte[] Endorsement { get; set; } = [];

    /// <summary>The instance key's signature over every preceding field, 64 bytes.</summary>
    [JsonPropertyName("sig")]
    public byte[] Signature { get; set; } = [];

    // ------------------------------------------------------------------ signing

    /// <summary>
    /// What the lab key signs once, when a console instance is minted. Keeping the
    /// endorsement separate from the instance certificate is what keeps a beacon inside one
    /// small datagram.
    /// </summary>
    public static byte[] EndorsementContent(string labId, string instanceId, byte[] publicKey) =>
        Encoding.UTF8.GetBytes($"{labId}|{instanceId}|{Convert.ToBase64String(publicKey)}");

    /// <summary>Produced when the instance certificate is issued and stored beside it.</summary>
    public static byte[] Endorse(LabKey lab, string instanceId, byte[] publicKey) =>
        lab.Sign(EndorsementContent(lab.LabId, instanceId, publicKey));

    /// <summary>What the instance key signs, on every beacon: all fields except the signature.</summary>
    public byte[] SignedContent() => Encoding.UTF8.GetBytes(string.Join('|',
    [
        Version.ToString(CultureInfo.InvariantCulture),
        LabId,
        InstanceId,
        InstanceName,
        Host,
        Port.ToString(CultureInfo.InvariantCulture),
        SentAtUnix.ToString(CultureInfo.InvariantCulture),
        TakeAtUnix.ToString(CultureInfo.InvariantCulture),
        Convert.ToBase64String(PublicKey),
        Convert.ToBase64String(Endorsement),
    ]));

    /// <summary>Signs this beacon with the console instance's private key.</summary>
    public Beacon SignWith(ECDsa instanceKey)
    {
        Signature = instanceKey.SignData(
            SignedContent(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return this;
    }

    // ------------------------------------------------------------------ wire format

    public byte[] ToDatagram()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > Defaults.BeaconMaxBytes)
        {
            throw new InvalidOperationException(
                $"The beacon is {bytes.Length} bytes; the limit is {Defaults.BeaconMaxBytes} " +
                $"(instance name '{InstanceName}' is probably too long).");
        }

        return bytes;
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out Beacon beacon)
    {
        beacon = null!;
        if (datagram.Length is 0 or > Defaults.BeaconMaxBytes)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Beacon>(datagram, Json);
            if (parsed is null)
            {
                return false;
            }

            beacon = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// Endorsement, then signature, then timestamp — the order PROTOCOL specifies, and the
    /// order that does the cheapest rejection first for anything not even claiming this lab.
    /// </summary>
    public bool TryVerify(X509Certificate2 authority, string labId, DateTimeOffset now, out BeaconFailure failure)
    {
        if (Version != Defaults.BeaconVersion)
        {
            failure = BeaconFailure.WrongVersion;
            return false;
        }

        if (InstanceId.Length == 0 || Host.Length == 0 || Port is <= 0 or > 65535 ||
            PublicKey.Length != P256.CompressedLength || Endorsement.Length == 0 || Signature.Length == 0 ||
            !IsRepresentable(SentAtUnix) || !IsRepresentable(TakeAtUnix))
        {
            failure = BeaconFailure.Malformed;
            return false;
        }

        if (!string.Equals(LabId, labId, StringComparison.OrdinalIgnoreCase))
        {
            failure = BeaconFailure.WrongLab;
            return false;
        }

        if (!LabKey.Verify(authority, EndorsementContent(LabId, InstanceId, PublicKey), Endorsement))
        {
            failure = BeaconFailure.BadEndorsement;
            return false;
        }

        if (!P256.TryDecompress(PublicKey, out var instanceKey))
        {
            failure = BeaconFailure.Malformed;
            return false;
        }

        using (instanceKey)
        {
            bool signatureValid;
            try
            {
                signatureValid = instanceKey.VerifyData(
                    SignedContent(), Signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException)
            {
                signatureValid = false;
            }

            if (!signatureValid)
            {
                failure = BeaconFailure.BadSignature;
                return false;
            }
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(SentAtUnix);
        if (age.Duration() > Defaults.BeaconMaxSkew)
        {
            failure = BeaconFailure.StaleTimestamp;
            return false;
        }

        failure = BeaconFailure.None;
        return true;
    }

    /// <summary>
    /// A timestamp the rest of this class may do arithmetic on. A forged datagram can carry
    /// any 64-bit number, and <see cref="DateTimeOffset.FromUnixTimeSeconds"/> throws outside
    /// its range — a beacon must always be rejected, never raise out of the receive loop.
    /// </summary>
    private static bool IsRepresentable(long unixSeconds) =>
        unixSeconds >= 0 && unixSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    public string Endpoint => $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => $"{InstanceName} ({InstanceId}) at {Endpoint}";
}

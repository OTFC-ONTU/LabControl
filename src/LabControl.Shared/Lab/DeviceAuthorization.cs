using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Discovery;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;

namespace LabControl.Shared.Lab;

/// <summary>What <see cref="DeviceAuthorization.Approve"/> produced: the grant to write and the leaf it certifies.</summary>
public sealed record DeviceGrantResult(DeviceGrantDocument Grant, DeviceGrantPayload Payload, X509Certificate2 Certificate, string InstanceId, string InstanceName, bool IsRenewal, string PublicKeyFingerprint);

/// <summary>
/// What <see cref="DeviceAuthorization.ImportGrant"/> produced: the instance document to
/// save, the snapshot to apply, and the keystore items that are superseded — to be forgotten
/// with <see cref="DeviceAuthorization.ForgetReplaced"/> only <b>after</b> the documents are
/// saved, so a failure in between leaves the device with a key it can still use.
/// </summary>
public sealed record DeviceGrantImport(InstanceDocument Instance, LabFilePayload Snapshot, DateTimeOffset ExpiresAt, bool IsRenewal, IReadOnlyList<ProtectedSecret> Replaced);

/// <summary>
/// The offline authorization exchange (M5, D-56 item 4). A teacher device makes a pending
/// key and writes a <c>.lcreq</c>; the administrator verifies it, issues a leaf under the
/// lab key and writes a <c>.lcgrant</c>; the device checks the grant against the CA it
/// pinned from the lab file and only then has an identity that beacons. No password, no
/// online step, nothing shared but a public key.
/// </summary>
public static class DeviceAuthorization
{
    /// <summary>
    /// Writes (or re-writes) the device's request from its pending key, creating the key on
    /// the first call. A second call regenerates the same request from the same key, so a
    /// lost file costs nothing; only a grant consumes the key. A withdrawn device gets a
    /// fresh instance id and key here, whoever calls: its old id is revoked for good.
    /// <paramref name="freshKey"/> discards a pending key on purpose — the way out when the
    /// grant for that key was approved but lost, since the administrator refuses to certify
    /// the same key twice (D-56 item 4). The caller saves <paramref name="access"/>.
    /// </summary>
    public static DeviceRequestDocument CreateRequest(AccessDocument access, ISecretProtector protector, string labName, string consoleVersion, DateTimeOffset now, bool freshKey = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(access.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(access.InstanceName);
        if (access.InstanceName.Length > Defaults.MaxInstanceNameLength)
        {
            access.InstanceName = access.InstanceName[..Defaults.MaxInstanceNameLength].TrimEnd();
        }

        if (access.State == AccessState.Revoked)
        {
            Forget(access.PendingKey);
            access.PendingKey = null;
            access.InstanceId = Guid.NewGuid().ToString("d");
            access.RequestedAtUnix = 0;
            access.CsrFingerprint = null;
            access.AuthorizedAtUnix = 0;
            access.ExpiresUnix = 0;
            access.RevokedAtUnix = 0;
        }
        else if (freshKey && access.PendingKey is not null)
        {
            Forget(access.PendingKey);
            access.PendingKey = null;
            access.RequestedAtUnix = 0;
            access.CsrFingerprint = null;
        }

        using var key = OpenOrCreatePendingKey(access, protector);
        var csr = LabCertificates.CreateDeviceSigningRequest(key, access.InstanceName);

        var payload = new DeviceRequestPayload
        {
            LabId = access.LabId,
            InstanceId = access.InstanceId,
            InstanceName = access.InstanceName,
            RequestedAccess = DeviceRequestPayload.TeacherAccess,
            CreatedAtUnix = access.RequestedAtUnix != 0 ? access.RequestedAtUnix : now.ToUnixTimeSeconds(),
            Csr = csr,
            ConsoleVersion = consoleVersion,
        };

        var bytes = SignedEnvelope.PayloadBytes(payload);
        var document = new DeviceRequestDocument
        {
            LabId = access.LabId,
            LabName = labName,
            Payload = Convert.ToBase64String(bytes),
            Signature = SignedEnvelope.Sign(SignedEnvelope.DeviceRequestDomain, key, bytes),
        };

        if (access.RequestedAtUnix == 0)
        {
            access.RequestedAtUnix = now.ToUnixTimeSeconds();
        }

        // Of the key, not of the CSR bytes: ECDSA signatures are randomised, so two CSRs from
        // the same key differ byte for byte while being the same request.
        access.CsrFingerprint = JsonStore.Fingerprint(key.ExportSubjectPublicKeyInfo());
        if (access.State is AccessState.NeedsAuthorization or AccessState.Unknown or AccessState.Revoked or AccessState.Expired)
        {
            access.State = AccessState.RequestPending;
        }

        return document;
    }

    public static string SerializeRequest(DeviceRequestDocument document) => JsonStore.Serialize(document, DeviceRequestDocument.Migrations);

    public static DeviceRequestDocument ParseRequest(string json, string fileName)
    {
        var document = JsonStore.Parse<DeviceRequestDocument>(json, fileName, DeviceRequestDocument.Migrations);
        if (!string.Equals(document.Kind, DeviceRequestDocument.KindValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{fileName}' is a {LabFile.Describe(document.Kind)}, not a device request.");
        }

        return document;
    }

    /// <summary>
    /// Verifies a request and reads it: the CSR's self-signature proves the device holds the
    /// key, the envelope signature by that key proves the request is the device's own words.
    /// </summary>
    public static DeviceRequestPayload OpenRequest(DeviceRequestDocument document, string fileName, out ECDsa deviceKey)
    {
        var bytes = SignedEnvelope.DecodePayload(document, fileName);
        var payload = SignedEnvelope.ParsePayload<DeviceRequestPayload>(bytes, fileName);

        if (!Guid.TryParseExact(payload.InstanceId, "d", out _))
        {
            throw new InvalidDataException($"'{fileName}' names a device id that is not a UUID.");
        }

        if (string.IsNullOrWhiteSpace(payload.InstanceName) || payload.InstanceName.Length > Defaults.MaxInstanceNameLength
            || payload.InstanceName.Any(c => char.IsControl(c)))
        {
            throw new InvalidDataException($"'{fileName}' names its device with an empty, overlong or unprintable name; a name has 1 to {Defaults.MaxInstanceNameLength} printable characters.");
        }

        if (!string.Equals(payload.RequestedAccess, DeviceRequestPayload.TeacherAccess, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{fileName}' asks for '{payload.RequestedAccess}' access; only teacher access can be granted from a request.");
        }

        if (!string.Equals(payload.LabId, document.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{fileName}' names lab {document.LabId} outside and {payload.LabId} inside.");
        }

        ECDsa key;
        try
        {
            key = LabCertificates.PublicKeyOfSigningRequest(payload.Csr);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or System.Formats.Asn1.AsnContentException or InvalidOperationException)
        {
            throw new InvalidDataException($"'{fileName}' carries a signing request this console cannot read: {ex.Message}", ex);
        }

        if (!SignedEnvelope.Verify(SignedEnvelope.DeviceRequestDomain, key, bytes, document.Signature))
        {
            key.Dispose();
            throw new InvalidDataException($"'{fileName}' is not signed by the key inside it; the file is damaged or was altered.");
        }

        deviceKey = key;
        return payload;
    }

    /// <summary>
    /// The administrator's side: issues the device leaf under the lab key and wraps it with
    /// the endorsement and a lab snapshot into a CA-signed grant. A request from a teacher
    /// device already in <paramref name="labDocument"/> is a renewal under the same instance
    /// id, and must carry a fresh key — the key already certified is never certified twice.
    /// A request naming this machine, an administrator machine or any record that is not a
    /// teacher device is refused: approving it would let a withdrawal of "that device" revoke
    /// the administrator everywhere. A withdrawn device (<c>instance:</c> entry) is refused,
    /// whatever it asks.
    /// </summary>
    public static DeviceGrantResult Approve(LabKey lab, DeviceRequestDocument request, string fileName, LabDocument labDocument, RevocationSet revocations, LabFilePayload snapshot, DateTimeOffset now)
    {
        var payload = OpenRequest(request, fileName, out var deviceKey);
        using (deviceKey)
        {
            if (!string.Equals(payload.LabId, lab.LabId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{fileName}' is a request for lab {payload.LabId}, not \"{lab.LabName}\".");
            }

            if (revocations.IsRevoked(LabCertificates.InstanceSerial(payload.InstanceId)))
            {
                throw new InvalidDataException($"'{fileName}' comes from a device whose access was withdrawn ({payload.InstanceName}); a new request from that device needs a fresh identity.");
            }

            var existing = labDocument.Instances.FirstOrDefault(i => string.Equals(i.InstanceId, payload.InstanceId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && (existing.IsThisMachine || existing.Access != ProfileAccess.Teacher))
            {
                throw new InvalidDataException(existing.IsThisMachine
                    ? $"'{fileName}' names this console's own device id; a teacher device has an id of its own, so this request was not made by one."
                    : $"'{fileName}' names the id of a console that holds this lab as {(existing.Access == ProfileAccess.Administrator ? "an administrator" : "a machine of unknown authority")} ({(existing.Name.Length > 0 ? existing.Name : existing.InstanceId)}); it cannot be re-issued as a teacher device.");
            }

            var isRenewal = existing is { AuthorizedAtUnix: > 0 };
            var fingerprint = JsonStore.Fingerprint(deviceKey.ExportSubjectPublicKeyInfo());
            if (existing is not null && string.Equals(existing.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{fileName}' carries the key this lab already certified for {payload.InstanceName} on {DateTimeOffset.FromUnixTimeSeconds(existing.AuthorizedAtUnix):yyyy-MM-dd}; import the grant written then, or write a new request on the device with a fresh key.");
            }

            var certificate = LabCertificates.IssueTeacherDevice(lab.Authority, lab.LabId, payload.InstanceId, payload.InstanceName, payload.Csr, now);
            var grantPayload = new DeviceGrantPayload
            {
                LabId = lab.LabId,
                InstanceId = payload.InstanceId,
                Certificate = certificate.Export(X509ContentType.Cert),
                Endorsement = Beacon.Endorse(lab, payload.InstanceId, P256.Compress(deviceKey)),
                IssuedAtUnix = now.ToUnixTimeSeconds(),
                ExpiresUnix = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds(),
                Snapshot = snapshot,
            };

            var bytes = SignedEnvelope.PayloadBytes(grantPayload);
            var grant = new DeviceGrantDocument
            {
                LabId = lab.LabId,
                LabName = lab.LabName,
                Payload = Convert.ToBase64String(bytes),
                Signature = SignedEnvelope.Sign(SignedEnvelope.DeviceGrantDomain, lab, bytes),
            };

            return new DeviceGrantResult(grant, grantPayload, certificate, payload.InstanceId, payload.InstanceName, isRenewal, fingerprint);
        }
    }

    public static string SerializeGrant(DeviceGrantDocument document) => JsonStore.Serialize(document, DeviceGrantDocument.Migrations);

    public static DeviceGrantDocument ParseGrant(string json, string fileName)
    {
        var document = JsonStore.Parse<DeviceGrantDocument>(json, fileName, DeviceGrantDocument.Migrations);
        if (!string.Equals(document.Kind, DeviceGrantDocument.KindValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{fileName}' is a {LabFile.Describe(document.Kind)}, not a device grant.");
        }

        return document;
    }

    /// <summary>Verifies a grant against the pinned CA and reads it; nothing about the device is checked yet.</summary>
    public static DeviceGrantPayload OpenGrant(DeviceGrantDocument document, string fileName, X509Certificate2 pinnedAuthority)
    {
        var bytes = SignedEnvelope.DecodePayload(document, fileName);
        if (!SignedEnvelope.Verify(SignedEnvelope.DeviceGrantDomain, pinnedAuthority, bytes, document.Signature))
        {
            throw new InvalidDataException($"'{fileName}' is not signed by the key of the lab saved here; the file is damaged, altered or for another lab.");
        }

        var payload = SignedEnvelope.ParsePayload<DeviceGrantPayload>(bytes, fileName);
        if (!string.Equals(payload.LabId, document.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{fileName}' names lab {document.LabId} outside and {payload.LabId} inside.");
        }

        return payload;
    }

    /// <summary>
    /// The device's side: the grant must be for this pending identity — same instance id,
    /// the very public key the request carried — and its leaf must validate as a console of
    /// this lab. The pending key becomes the instance key under its normal keystore name.
    /// Nothing is forgotten here: the pending item (and, on a renewal, a previous key held
    /// under another name) come back in <see cref="DeviceGrantImport.Replaced"/> for the
    /// caller to forget after <c>instance.json</c> and <c>access.json</c> are saved, so a
    /// keystore that fails half-way leaves the device able to re-request under the same id.
    /// </summary>
    public static DeviceGrantImport ImportGrant(DeviceGrantDocument document, string fileName, AccessDocument access, X509Certificate2 pinnedAuthority, RevocationSet? revocations, ISecretProtector protector, InstanceDocument? previous, DateTimeOffset now)
    {
        var payload = OpenGrant(document, fileName, pinnedAuthority);

        if (!string.Equals(payload.LabId, access.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{fileName}' is a grant for lab {payload.LabId}, not this one.");
        }

        if (!string.Equals(payload.InstanceId, access.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{fileName}' grants access to device {payload.InstanceId}, not to this device ({access.InstanceId}); write a new request here and have it approved.");
        }

        if (access.PendingKey is null)
        {
            throw new InvalidDataException($"'{fileName}' answers a request this device did not write, or one already answered; write a new request first.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(payload.Certificate);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"'{fileName}' carries a certificate that cannot be read.", ex);
        }

        using (certificate)
        {
            if (!SecretProtector.For(access.PendingKey).TryUnprotect(access.PendingKey, out var pkcs8))
            {
                throw new InvalidDataException($"The pending key of this device could not be opened ({access.PendingKey.Protector}); write a new request.");
            }

            using var key = LabCertificates.CreateKey();
            try
            {
                key.ImportPkcs8PrivateKey(pkcs8, out _);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }

            using var granted = certificate.GetECDsaPublicKey();
            if (granted is null || !granted.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
            {
                throw new InvalidDataException($"'{fileName}' certifies a different key than the one this device's request carried; write a new request and have it approved.");
            }

            var trust = new LabTrust(pinnedAuthority, access.LabId);
            if (!trust.TryValidate(certificate, LabRole.Console, revocations, out var name, out var failure, now))
            {
                throw new InvalidDataException($"'{fileName}' carries a certificate this lab does not accept: {LabTrust.Describe(failure, name)}.");
            }

            if (!string.Equals(name.Id, access.InstanceId, StringComparison.OrdinalIgnoreCase) || name.Access != ConsoleAccess.Teacher)
            {
                throw new InvalidDataException($"'{fileName}' carries a certificate for another device or another kind of access.");
            }

            if (!LabKey.Verify(pinnedAuthority, Beacon.EndorsementContent(access.LabId, access.InstanceId, P256.Compress(key)), payload.Endorsement))
            {
                throw new InvalidDataException($"'{fileName}' carries an endorsement the lab key did not sign.");
            }

            var isRenewal = previous is not null;
            var pending = access.PendingKey;
            var reference = ConsoleInstance.ProtectionReference(access.InstanceId);

            // The pending key moves under the instance key's name: the same bytes are protected
            // again under that name (a keystore replaces an item of the same reference), and only
            // then does the document change. Until the caller saves, the pending key is still
            // the device's key on disk, so a failure here costs nothing.
            var exported = key.ExportPkcs8PrivateKey();
            ProtectedSecret stored;
            try
            {
                stored = protector.Protect(reference, exported);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exported);
            }

            // What the caller forgets after saving: the pending item always; a previous instance
            // key only when it lives under another reference or protector — under the same one
            // it was replaced by the Protect above, and forgetting it would delete the new key.
            var replaced = new List<ProtectedSecret> { pending };
            if (previous is not null
                && (!string.Equals(previous.PrivateKey.Reference, stored.Reference, StringComparison.Ordinal)
                    || !string.Equals(previous.PrivateKey.Protector, stored.Protector, StringComparison.Ordinal)))
            {
                replaced.Add(previous.PrivateKey);
            }

            var instance = new InstanceDocument
            {
                LabId = access.LabId,
                InstanceId = access.InstanceId,
                InstanceName = access.InstanceName,
                Certificate = payload.Certificate,
                Endorsement = payload.Endorsement,
                PrivateKey = stored,
                CreatedAtUnix = now.ToUnixTimeSeconds(),
                RecoveryCodeAcknowledged = true,
                BackupExportedAtUnix = previous?.BackupExportedAtUnix ?? 0,
                BackupLocation = previous?.BackupLocation,
                BackupFingerprint = previous?.BackupFingerprint,
            };

            var expires = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            access.PendingKey = null;
            access.CsrFingerprint = null;
            access.RequestedAtUnix = 0;
            access.State = AccessState.Authorized;
            access.AuthorizedAtUnix = now.ToUnixTimeSeconds();
            access.ExpiresUnix = expires.ToUnixTimeSeconds();
            access.RevokedAtUnix = 0;

            return new DeviceGrantImport(instance, payload.Snapshot, expires, isRenewal, replaced);
        }
    }

    /// <summary>Forgets the keystore items a grant import superseded; call once the documents are saved.</summary>
    public static void ForgetReplaced(DeviceGrantImport import)
    {
        foreach (var secret in import.Replaced)
        {
            Forget(secret);
        }
    }

    /// <summary>A file name that says which device and which lab: <c>MacBook – ОНТФК lab 214.lcreq</c>.</summary>
    public static string RequestFileName(string instanceName, string labName) =>
        $"{LabFile.SafeName(instanceName)} – {LabFile.SafeName(labName)}{Defaults.DeviceRequestFileExtension}";

    /// <summary>The grant is written beside the request it answers, with the grant extension.</summary>
    public static string GrantPathFor(string requestPath) => Path.ChangeExtension(requestPath, Defaults.DeviceGrantFileExtension);

    private static ECDsa OpenOrCreatePendingKey(AccessDocument access, ISecretProtector protector)
    {
        if (access.PendingKey is not null && SecretProtector.For(access.PendingKey).TryUnprotect(access.PendingKey, out var pkcs8))
        {
            var existing = LabCertificates.CreateKey();
            try
            {
                existing.ImportPkcs8PrivateKey(pkcs8, out _);
                return existing;
            }
            catch (CryptographicException)
            {
                existing.Dispose();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }

        Forget(access.PendingKey);
        var key = LabCertificates.CreateKey();
        var exported = key.ExportPkcs8PrivateKey();
        try
        {
            access.PendingKey = protector.Protect(AccessDocument.PendingKeyReference(access.InstanceId), exported);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }

        return key;
    }

    private static void Forget(ProtectedSecret? secret)
    {
        if (secret is null)
        {
            return;
        }

        try
        {
            SecretProtector.For(secret).Forget(secret);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // An item nobody can open is inert once its document forgets it.
        }
    }
}

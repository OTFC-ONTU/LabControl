using System.Security.Cryptography.X509Certificates;
using LabControl.Console.Localization;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabControl.Console.Services;

/// <summary>What <see cref="DeviceAccess.ApproveRequest"/> did, for the results row and the event.</summary>
public sealed record ApprovedRequest(string GrantPath, string InstanceId, string InstanceName, bool IsRenewal, DateTimeOffset ExpiresAt);

/// <summary>
/// The console side of lab files and device authorization (M5, D-56): importing a
/// <c>.lclab</c> into a new or saved lab, writing a device's <c>.lcreq</c>, importing the
/// <c>.lcgrant</c> that answers it, and — on an administrator profile — approving requests.
/// Every step works on the saved lab's files, or on the running session when that lab is
/// active, so nothing written here is overwritten by the next <c>SaveLab</c>.
/// </summary>
public sealed class DeviceAccess
{
    private readonly ConsoleBootstrap _bootstrap;
    private readonly Func<string, LabSession?> _active;
    private readonly Func<ISecretProtector> _newProtector;
    private readonly ILogger _log;

    /// <param name="active">The running session for a lab id, or <c>null</c>: imports into an active lab go through it.</param>
    public DeviceAccess(ConsoleBootstrap bootstrap, Func<string, LabSession?> active, ILogger? log = null, Func<ISecretProtector>? newProtector = null)
    {
        _bootstrap = bootstrap;
        _active = active;
        _log = log ?? NullLogger.Instance;
        _newProtector = newProtector ?? bootstrap.NewProtector;
    }

    /// <summary>The console's own version, written into requests so the administrator can see what asked.</summary>
    public static string ConsoleVersion => typeof(DeviceAccess).Assembly.GetName().Version?.ToString(3) ?? string.Empty;

    // ------------------------------------------------------------------ lab file

    /// <summary>
    /// Imports a lab file (D-56 item 3). A lab this device has never seen becomes a teacher
    /// profile that needs authorization, with a pending identity that beacons nothing; a lab
    /// already saved is refreshed by the merge that cannot go backwards, whatever its access.
    /// </summary>
    public ImportFileResult ImportLabFile(string path, string instanceName)
    {
        var fileName = Path.GetFileName(path);
        var document = LabFile.Parse(File.ReadAllText(path), fileName);
        var existing = _bootstrap.Profiles.Find(document.LabId);

        if (existing is null)
        {
            var payload = LabFile.Open(document, fileName);
            return new ImportFileResult(path, true, CreateTeacherProfile(payload, instanceName), payload.LabName);
        }

        var store = _bootstrap.StoreFor(existing.LabId);
        using var pinned = X509CertificateLoader.LoadCertificate(PinnedAuthority(existing, store));
        var snapshot = LabFile.Open(document, fileName, pinned);
        var report = Refresh(existing, store, snapshot, pinned, importScripts: true);
        return new ImportFileResult(path, true, DescribeRefresh(existing.LabName, report), existing.LabName);
    }

    private string CreateTeacherProfile(LabFilePayload payload, string instanceName)
    {
        var store = _bootstrap.StoreFor(payload.LabId);
        if (Directory.Exists(store.Directory) && (store.HasLabKey || File.Exists(store.InstancePath) || store.HasAccess))
        {
            throw new InvalidDataException(Strings.Format("Bootstrap.LabDirectoryOccupied", store.Directory, payload.LabName, Defaults.ProfilesFileName));
        }

        using var authority = LabFile.LoadAuthority(payload.Authority, payload.LabId, payload.LabName, null);
        var now = DateTimeOffset.UtcNow;
        var access = new AccessDocument
        {
            LabId = payload.LabId,
            State = AccessState.NeedsAuthorization,
            InstanceId = Guid.NewGuid().ToString("d"),
            InstanceName = instanceName,
            Authority = payload.Authority,
        };

        var created = false;
        try
        {
            store.EnsureDirectories();
            created = true;
            store.SaveLab(LabFile.NewLabDocument(payload, authority));
            if (payload.Scripts is not null)
            {
                payload.Scripts.LabId = payload.LabId;
                store.SaveScripts(payload.Scripts);
            }

            // The pending identity: a key pair under the keystore and a fresh instance id. No
            // certificate yet, so nothing beacons until the grant arrives (D-56 item 3).
            DeviceAuthorization.CreateRequest(access, _newProtector(), payload.LabName, ConsoleVersion, now);
            access.State = AccessState.NeedsAuthorization;
            access.RequestedAtUnix = 0;
            access.CsrFingerprint = null;
            store.SaveAccess(access);

            _bootstrap.Profiles.Upsert(new ProfileRecord
            {
                LabId = payload.LabId,
                LabName = payload.LabName,
                AuthorityFingerprint = ProfileRecord.AuthorityFingerprintOf(payload.Authority),
                Access = ProfileAccess.Teacher,
                Authorization = ProfileAuthorization.NeedsAuthorization,
                InstanceId = access.InstanceId,
                InstanceName = instanceName,
                PcCount = payload.Roster.Count,
                AddedAtUnix = now.ToUnixTimeSeconds(),
                Source = ProfileSource.LabFile,
            });

            _log.LogInformation("Lab {LabId} added from a lab file issued by {Issuer}: teacher profile, needs authorization", payload.LabId, payload.IssuedByInstanceName);
            return Strings.Format("Import.LabFileAdded", payload.LabName, payload.Roster.Count);
        }
        catch
        {
            if (access.PendingKey is not null)
            {
                try
                {
                    SecretProtector.For(access.PendingKey).Forget(access.PendingKey);
                }
                catch (Exception forget) when (forget is InvalidDataException or IOException or InvalidOperationException)
                {
                    _log.LogWarning(forget, "The pending key of lab {LabId} could not be removed from the keystore", payload.LabId);
                }
            }

            if (created)
            {
                try
                {
                    Directory.Delete(store.Directory, recursive: true);
                }
                catch (Exception delete) when (delete is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(delete, "The directory of the failed lab-file import could not be removed: {Directory}", store.Directory);
                }
            }

            throw;
        }
    }

    /// <summary>The merge into a saved lab, through the session when it runs; scripts only into an empty library.</summary>
    private LabFileMergeReport Refresh(ProfileRecord profile, LabStore store, LabFilePayload snapshot, X509Certificate2 authority, bool importScripts)
    {
        LabFileMergeReport report;
        var active = _active(profile.LabId);
        if (active is { IsDisposed: false })
        {
            report = active.ApplySnapshot(snapshot);
            profile.PcCount = active.Registry.Document.Machines.Count;
        }
        else
        {
            var document = store.LoadLab(profile.LabId, profile.LabName);
            var registry = new LabRegistry(document, authority);
            report = LabFile.Merge(document, snapshot, authority, registry.Revocations, DateTimeOffset.UtcNow);
            store.SaveLab(document);
            profile.PcCount = document.Machines.Count;
        }

        if (importScripts && snapshot.Scripts is not null && store.LoadScripts() is null && active is not { IsDisposed: false })
        {
            snapshot.Scripts.LabId = profile.LabId;
            store.SaveScripts(snapshot.Scripts);
        }

        _bootstrap.Profiles.Upsert(profile);
        _log.LogInformation("Lab {LabId} refreshed from a snapshot {Version}: {Added} added, {Updated} updated, {Kept} kept, layout {Layout}, {Revocations} revocation(s) new{Older}",
            profile.LabId, snapshot.SnapshotVersion, report.MachinesAdded, report.MachinesUpdated, report.MachinesKept, report.LayoutReplaced ? "replaced" : "kept", report.RevocationsAdded, report.OlderSnapshot ? " (older snapshot)" : string.Empty);
        return report;
    }

    private static string DescribeRefresh(string labName, LabFileMergeReport report) =>
        report.OlderSnapshot
            ? Strings.Format("Import.LabFileOlder", labName, report.MachinesAdded, report.RevocationsAdded)
            : Strings.Format("Import.LabFileRefreshed", labName, report.MachinesAdded, report.MachinesUpdated, report.RevocationsAdded);

    private static byte[] PinnedAuthority(ProfileRecord profile, LabStore store)
    {
        if (store.HasLabKey)
        {
            return store.LoadLabKey().Authority;
        }

        var access = store.LoadAccess() ?? throw new InvalidDataException(Strings.Format("Bootstrap.AccessMissing", Defaults.AccessFileName, Defaults.LabKeyFileName));
        return access.Authority;
    }

    // ------------------------------------------------------------------ request

    /// <summary>True when a profile can write a request now: a teacher profile that is unauthorized, withdrawn, expired, or within the renewal lead time.</summary>
    public static bool CanRequest(ProfileRecord profile, DateTimeOffset now) =>
        profile.Access == ProfileAccess.Teacher && (profile.Authorization != ProfileAuthorization.Authorized || NeedsRenewal(profile, now));

    public static bool NeedsRenewal(ProfileRecord profile, DateTimeOffset now) =>
        profile.Authorization == ProfileAuthorization.Authorized && profile.AccessExpiresUnix > 0
        && DateTimeOffset.FromUnixTimeSeconds(profile.AccessExpiresUnix) - now < Defaults.CertificateRenewalLeadTime;

    /// <summary>The name the save dialog suggests: <c>MacBook – Room 214.lcreq</c>.</summary>
    public string SuggestRequestFileName(string labId)
    {
        var profile = _bootstrap.Profiles.Find(labId) ?? throw new InvalidDataException(Strings.Format("Bootstrap.LabNotSaved", labId));
        return DeviceAuthorization.RequestFileName(profile.InstanceName.Length > 0 ? profile.InstanceName : ConsoleBootstrap.DefaultInstanceName(), profile.LabName);
    }

    /// <summary>
    /// Writes the device's request (D-56 item 4). A re-run regenerates the same request from
    /// the same pending key; a withdrawn device gets a fresh instance id and key, because the
    /// old id is revoked for good; a renewal keeps the id and takes a fresh key.
    /// </summary>
    public string WriteRequest(string labId, string path)
    {
        var profile = _bootstrap.Profiles.Find(labId) ?? throw new InvalidDataException(Strings.Format("Bootstrap.LabNotSaved", labId));
        if (profile.Access != ProfileAccess.Teacher)
        {
            throw new InvalidOperationException(Strings.Get("Access.RequestOnAdministrator"));
        }

        var store = _bootstrap.StoreFor(labId);
        var access = store.LoadAccess() ?? throw new InvalidDataException(Strings.Format("Bootstrap.AccessMissing", Defaults.AccessFileName, Defaults.LabKeyFileName));
        var now = DateTimeOffset.UtcNow;

        // A withdrawn device is given a fresh id and key by CreateRequest itself (its old id
        // is revoked for good); a renewal keeps the id and takes a fresh key.
        if (access.State is AccessState.Authorized or AccessState.Expired && access.PendingKey is null)
        {
            // A renewal (or a fresh start after expiry): same instance id, new key.
            access.RequestedAtUnix = 0;
            access.CsrFingerprint = null;
        }

        if (access.InstanceName.Length == 0)
        {
            access.InstanceName = profile.InstanceName.Length > 0 ? profile.InstanceName : ConsoleBootstrap.DefaultInstanceName();
        }

        var request = DeviceAuthorization.CreateRequest(access, _newProtector(), profile.LabName, ConsoleVersion, now);
        File.WriteAllText(path, DeviceAuthorization.SerializeRequest(request));
        store.SaveAccess(access);

        profile.InstanceId = access.InstanceId;
        profile.InstanceName = access.InstanceName;
        if (access.State == AccessState.RequestPending)
        {
            profile.Authorization = ProfileAuthorization.RequestPending;
        }

        _bootstrap.Profiles.Upsert(profile);
        _log.LogInformation("Device request for lab {LabId} written to {Path} (instance {Instance})", labId, path, access.InstanceId);
        return path;
    }

    // ------------------------------------------------------------------ grant

    /// <summary>Imports a grant (D-56 item 4): the pending identity becomes this device's instance and the snapshot refreshes the lab.</summary>
    public ImportFileResult ImportGrant(string path)
    {
        var fileName = Path.GetFileName(path);
        var document = DeviceAuthorization.ParseGrant(File.ReadAllText(path), fileName);
        var profile = _bootstrap.Profiles.Find(document.LabId)
                      ?? throw new InvalidDataException(Strings.Format("Import.GrantForUnknownLab", document.LabName));

        var store = _bootstrap.StoreFor(profile.LabId);
        var access = store.LoadAccess();
        if (profile.Access != ProfileAccess.Teacher || access is null)
        {
            throw new InvalidDataException(Strings.Format("Import.GrantOnAdministrator", profile.LabName));
        }

        using var pinned = X509CertificateLoader.LoadCertificate(access.Authority);
        var labDocument = store.LoadLab(profile.LabId, profile.LabName);
        var registry = new LabRegistry(labDocument, pinned);
        var previous = store.LoadInstance();
        var now = DateTimeOffset.UtcNow;

        var imported = DeviceAuthorization.ImportGrant(document, fileName, access, pinned, registry.Revocations, _newProtector(), previous, now);

        // The documents first, the keystore clean-up after: until instance.json names the new
        // key, the pending item is still this device's key, and a failure between the two
        // leaves a device that can re-request under the same id rather than one with no key.
        store.SaveInstance(imported.Instance);
        store.SaveAccess(access);
        DeviceAuthorization.ForgetReplaced(imported);

        profile.Authorization = ProfileAuthorization.Authorized;
        profile.AccessExpiresUnix = imported.ExpiresAt.ToUnixTimeSeconds();
        profile.InstanceId = imported.Instance.InstanceId;
        profile.InstanceName = imported.Instance.InstanceName;
        _bootstrap.Profiles.Upsert(profile);
        _log.LogInformation("Grant for lab {LabId} imported: device {Instance} authorized until {Expires}{Renewal}", profile.LabId, imported.Instance.InstanceId, imported.ExpiresAt, imported.IsRenewal ? " (renewal)" : string.Empty);

        var message = Strings.Format(imported.IsRenewal ? "Import.GrantRenewed" : "Import.GrantAccepted", profile.LabName, imported.ExpiresAt.ToLocalTime().ToString("d", Strings.Culture));

        // The snapshot is a courtesy: the device is authorized whether or not the merge lands.
        LabFileMergeReport report;
        try
        {
            report = Refresh(profile, store, imported.Snapshot, pinned, importScripts: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SchemaVersionException)
        {
            _log.LogWarning(ex, "Grant for lab {LabId} imported, but its snapshot could not be applied: {Message}", profile.LabId, ex.Message);
            return new ImportFileResult(path, true, message + " " + Strings.Format("Import.GrantSnapshotFailed", ex.Message), profile.LabName);
        }

        return new ImportFileResult(path, true, message + (report.MachinesAdded + report.RevocationsAdded > 0 ? " " + DescribeRefresh(profile.LabName, report) : string.Empty), profile.LabName);
    }

    // ------------------------------------------------------------------ approval (administrator)

    /// <summary>True when this device holds the key of the lab a request names — the only place a request can be approved.</summary>
    public bool CanApprove(string labId) =>
        _bootstrap.Profiles.Find(labId) is { Access: ProfileAccess.Administrator } && _bootstrap.StoreFor(labId).HasLabKey;

    /// <summary>Reads a request far enough to name its lab and device before the key is unlocked.</summary>
    public static DeviceRequestDocument ReadRequest(string path) =>
        DeviceAuthorization.ParseRequest(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>
    /// Approves a request with the unlocked key (D-56 item 4) and writes the grant beside it.
    /// The device book in <c>lab.json</c> records the authorization; the running session,
    /// when this lab is active, raises <c>device.authorized</c>.
    /// </summary>
    public ApprovedRequest ApproveRequest(string path, LabKey lab)
    {
        var fileName = Path.GetFileName(path);
        var request = DeviceAuthorization.ParseRequest(File.ReadAllText(path), fileName);
        if (!string.Equals(request.LabId, lab.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Strings.Format("Import.RequestForOtherLab", request.LabName));
        }

        var now = DateTimeOffset.UtcNow;
        var active = _active(lab.LabId);
        var store = _bootstrap.StoreFor(lab.LabId);
        var snapshot = _bootstrap.SnapshotFor(lab, active, now);

        DeviceGrantResult result;
        if (active is { IsDisposed: false })
        {
            result = DeviceAuthorization.Approve(lab, request, fileName, active.Registry.Document, active.Registry.Revocations, snapshot, now);
            using (result.Certificate)
            {
                active.Registry.RecordAuthorization(result.InstanceId, result.InstanceName, LabCertificates.SerialOf(result.Certificate), result.PublicKeyFingerprint, now);
                active.SaveLab();
                active.Events.Info("device.authorized", $"{result.InstanceName} authorized as a teacher device{(result.IsRenewal ? " (renewal)" : string.Empty)}; access until {DateTimeOffset.FromUnixTimeSeconds(result.Payload.ExpiresUnix):yyyy-MM-dd}.");
            }
        }
        else
        {
            var labDocument = store.LoadLab(lab.LabId, lab.LabName);
            var registry = new LabRegistry(labDocument, lab.Authority);
            result = DeviceAuthorization.Approve(lab, request, fileName, labDocument, registry.Revocations, snapshot, now);
            using (result.Certificate)
            {
                registry.RecordAuthorization(result.InstanceId, result.InstanceName, LabCertificates.SerialOf(result.Certificate), result.PublicKeyFingerprint, now);
                store.SaveLab(labDocument);
            }
        }

        var grantPath = DeviceAuthorization.GrantPathFor(path);
        File.WriteAllText(grantPath, DeviceAuthorization.SerializeGrant(result.Grant));
        _log.LogInformation("Request {File} approved: device {Instance} ({Name}); grant written to {Grant}", fileName, result.InstanceId, result.InstanceName, grantPath);
        return new ApprovedRequest(grantPath, result.InstanceId, result.InstanceName, result.IsRenewal, DateTimeOffset.FromUnixTimeSeconds(result.Payload.ExpiresUnix));
    }
}

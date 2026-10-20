using System.Security.Cryptography;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;

namespace LabControl.Console.Services;

/// <summary>
/// Remembers a lab key on this computer (D-82): a key holder named for this console instance
/// whose secret — 32 random bytes, not a passphrase — lives only in the OS store this console
/// already keeps its own instance key in, with the reference in <c>device-key.json</c>. The
/// passphrase and the recovery code are untouched and still needed for a backup and for any
/// other computer: the holder entry travels with the key document, its secret never does.
/// Nothing here throws on a missing or unreadable secret — the key is then simply not
/// remembered, and the teacher is asked for the passphrase as before.
/// </summary>
public sealed class LabKeyMemory
{
    private readonly LabStore _store;
    private readonly string _instanceId;
    private readonly Func<ISecretProtector> _protector;

    /// <param name="protector">The store a new secret is written to — the one that holds this instance's own key.</param>
    public LabKeyMemory(LabStore store, string instanceId, Func<ISecretProtector> protector)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(protector);
        _store = store;
        _instanceId = instanceId;
        _protector = protector;
    }

    /// <summary>The key holder this computer adds: <see cref="Defaults.DeviceHolderPrefix"/> plus the instance id.</summary>
    public string HolderName => HolderNameOf(_instanceId);

    public static string HolderNameOf(string instanceId) => Defaults.DeviceHolderPrefix + instanceId;

    /// <summary>True for a holder some computer added to remember the key, rather than a person's passphrase.</summary>
    public static bool IsDeviceHolder(string holderName) => holderName.StartsWith(Defaults.DeviceHolderPrefix, StringComparison.Ordinal);

    /// <summary>True when this computer can open <paramref name="document"/> on its own.</summary>
    public bool IsRemembered(LabKeyDocument document)
    {
        var device = Load();
        return device is not null && string.Equals(device.LabId, document.LabId, StringComparison.OrdinalIgnoreCase)
            && document.Wrappings.Any(w => w.Kind == KeyWrappingKind.Holder && string.Equals(w.Name, device.HolderName, StringComparison.Ordinal));
    }

    /// <summary>Opens the key with the remembered secret, or <c>null</c> when it is not remembered here or the secret cannot be read.</summary>
    public LabKey? TryOpen(LabKeyDocument document)
    {
        var device = Load();
        if (device is null || !string.Equals(device.LabId, document.LabId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        byte[] secret;
        try
        {
            if (!SecretProtector.For(device.Secret).TryUnprotect(device.Secret, out secret))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or CryptographicException or PlatformNotSupportedException)
        {
            return null;
        }

        try
        {
            return LabKey.TryUnlock(document, device.HolderName, Convert.ToBase64String(secret), out var key) ? key : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Adds this computer's holder to the unlocked <paramref name="key"/> (replacing an earlier
    /// one) and stores its secret. The caller saves the key document afterwards; a key document
    /// saved without <c>device-key.json</c> only leaves a holder nobody can use.
    /// </summary>
    public void Remember(LabKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Forget(key);

        var secret = RandomNumberGenerator.GetBytes(Defaults.DeviceHolderSecretBytes);
        try
        {
            var protector = _protector();
            var reference = $"labkey-{_instanceId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
            var stored = protector.Protect(reference, secret);
            key.AddHolder(HolderName, Convert.ToBase64String(secret), iterations: Defaults.DeviceHolderIterations);
            _store.SaveDeviceKey(new DeviceKeyDocument
            {
                LabId = key.LabId,
                HolderName = HolderName,
                RememberedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Secret = stored,
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Forgets the key on this computer: the secret out of the OS store, <c>device-key.json</c>
    /// deleted, and — when the key is open — this computer's holder removed from it (the caller
    /// saves the document). Safe to call when nothing is remembered.
    /// </summary>
    public void Forget(LabKey? key)
    {
        if (Load() is { } device)
        {
            try
            {
                SecretProtector.For(device.Secret).Forget(device.Secret);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or CryptographicException or PlatformNotSupportedException)
            {
                // A secret that cannot be reached is as good as gone; the file goes regardless.
            }

            _store.DeleteDeviceKey();
        }

        if (key is not null && key.HolderNames.Contains(HolderName, StringComparer.Ordinal))
        {
            key.RemoveHolder(HolderName);
        }
    }

    private DeviceKeyDocument? Load()
    {
        try
        {
            var device = _store.LoadDeviceKey();
            return device is not null && string.Equals(device.HolderName, HolderName, StringComparison.Ordinal) ? device : null;
        }
        catch (Exception)
        {
            // A torn, foreign or newer file means "not remembered here": the passphrase still works.
            return null;
        }
    }
}

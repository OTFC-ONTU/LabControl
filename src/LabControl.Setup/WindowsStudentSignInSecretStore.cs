using System.Security.Cryptography;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Authentication.Identity;

namespace LabControl.Setup;

/// <summary>Local LSA DefaultPassword only. Never converts secret data to a string.
/// The account guard is repeated at the native boundary; it cannot isolate SAM/LSA
/// from another administrator between the last check and write.</summary>
internal sealed class WindowsStudentSignInSecretStore(Action requireAccount) : IStudentSignInSecretStore
{
    public byte[]? Read()
    {
        requireAccount();
        using var policy = Open(write: false);
        return Read(policy);
    }

    public unsafe void Write(byte[]? expected, byte[]? value)
    {
        Validate(expected);
        Validate(value);
        requireAccount();
        using var policy = Open(write: true);
        var current = Read(policy);
        try { if (!Equal(current, expected)) throw Failure(); }
        finally { Clear(current); }
        requireAccount();
        // Keep a non-null buffer for an existing empty secret; null PrivateData deletes.
        var buffer = new byte[(value?.Length ?? 0) + 2];
        try
        {
            value?.CopyTo(buffer, 0);
            fixed (byte* data = buffer)
            fixed (char* name = Defaults.AutoLogonSecretName)
            {
                var key = Name(name);
                var secret = new LSA_UNICODE_STRING
                {
                    Buffer = new PWSTR((char*)data), Length = (ushort)(value?.Length ?? 0),
                    MaximumLength = (ushort)Math.Min(buffer.Length, ushort.MaxValue),
                };
                Check(PInvoke.LsaStorePrivateData(policy, key, value is null ? null : secret));
            }
        }
        finally { Clear(buffer); }
        var actual = Read(policy);
        try { if (!Equal(actual, value)) throw Failure(); }
        finally { Clear(actual); }
        requireAccount();
    }

    private static unsafe LsaCloseSafeHandle Open(bool write)
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = (uint)sizeof(LSA_OBJECT_ATTRIBUTES) };
        var status = PInvoke.LsaOpenPolicy(null, attributes,
            (uint)(PInvoke.POLICY_GET_PRIVATE_INFORMATION | (write ? PInvoke.POLICY_CREATE_SECRET : 0)), out var handle);
        if (status.Value != 0) { handle.Dispose(); throw Failure(); }
        return handle;
    }

    private static unsafe byte[]? Read(LsaCloseSafeHandle policy)
    {
        LSA_UNICODE_STRING* data = null;
        try
        {
            fixed (char* name = Defaults.AutoLogonSecretName)
            {
                var status = PInvoke.LsaRetrievePrivateData(policy, Name(name), out data);
                if (status == NTSTATUS.STATUS_OBJECT_NAME_NOT_FOUND) return null;
                Check(status);
            }
            if (data is null || data->Length % 2 != 0 || data->Length > data->MaximumLength
                || (data->Length != 0 && data->Buffer.Value is null)) throw Failure();
            return new ReadOnlySpan<byte>(data->Buffer.Value, data->Length).ToArray();
        }
        finally
        {
            if (data is not null)
            {
                if (data->Buffer.Value is not null)
                    CryptographicOperations.ZeroMemory(new Span<byte>(data->Buffer.Value, Math.Min(data->Length, data->MaximumLength)));
                PInvoke.LsaFreeMemory(data);
            }
        }
    }

    private static unsafe LSA_UNICODE_STRING Name(char* name) => new()
    {
        Buffer = new PWSTR(name), Length = (ushort)(Defaults.AutoLogonSecretName.Length * 2),
        MaximumLength = (ushort)((Defaults.AutoLogonSecretName.Length + 1) * 2),
    };

    private static void Validate(byte[]? bytes)
    {
        if (bytes is not null && (bytes.Length % 2 != 0 || bytes.Length > ushort.MaxValue - 1)) throw Failure();
    }

    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null
        : b is not null && CryptographicOperations.FixedTimeEquals(a, b);
    private static void Clear(byte[]? bytes) { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    private static void Check(NTSTATUS status) { if (status.Value != 0) throw Failure(); }
    private static IOException Failure() => new("The sign-in secret could not be read or changed safely; preserve it for review.");
}

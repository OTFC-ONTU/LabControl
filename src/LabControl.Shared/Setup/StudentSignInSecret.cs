using System.Security.Cryptography;
using System.Text;

namespace LabControl.Shared.Setup;

/// <summary>Only the fixed autologon secret. Values are raw UTF-16LE bytes; null means
/// absent, an empty array means an existing empty secret. Caller owns returned buffers.
/// Write must recheck expected immediately before mutation and verify read-back.</summary>
public interface IStudentSignInSecretStore
{
    byte[]? Read();
    void Write(byte[]? expected, byte[]? value);
}

/// <summary>Account-mode gate and typed journal adapter. Null result means skipped.
/// The future pipeline must coordinate Winlogon values before invoking this component.</summary>
public sealed class StudentSignInSecret(
    InstallationState state, Func<string?> currentStudentSid,
    Func<SetupSettingsJournal> journal, IStudentSignInSecretStore store)
{
    public SettingChangeResult? Apply()
    {
        if (!Enabled()) return null;
        using var setting = new SecretSetting(store, RequireAccount);
        var bytes = Encoding.Unicode.GetBytes(Defaults.StudentDefaultPassword);
        var desired = Encode(bytes);
        try { return journal().Apply(setting, desired); }
        finally { Clear(bytes); Clear(desired); }
    }

    public SettingChangeResult? Restore()
    {
        if (!Enabled()) return null;
        using var setting = new SecretSetting(store, RequireAccount);
        return journal().Restore(setting);
    }

    private bool Enabled()
    {
        var configuration = state.Read() ?? throw new InvalidOperationException("Installation history is required for sign-in settings.");
        if (configuration.CreateStudentAccount != true) return false;
        RequireAccount();
        return true;
    }

    private void RequireAccount() => state.RequireManagedStudent(currentStudentSid());

    private sealed class SecretSetting(IStudentSignInSecretStore native, Action guard) : ISetupSetting, IDisposable
    {
        private byte[]? _lastRead;
        private bool _hasRead;
        public string Id => "student.autologon-secret";

        public byte[]? Read()
        {
            Dispose();
            guard();
            var value = native.Read();
            try
            {
                var encoded = Encode(value);
                _lastRead = value;
                _hasRead = true;
                return encoded;
            }
            catch { Clear(value); throw; }
        }

        public void Write(byte[]? value)
        {
            byte[]? decoded = null;
            try
            {
                decoded = Decode(value);
                if (!_hasRead) throw new InvalidOperationException("Read the sign-in setting before changing it.");
                guard();
                native.Write(_lastRead, decoded);
            }
            finally { Clear(decoded); Dispose(); }
        }

        public void Dispose()
        {
            Clear(_lastRead);
            _lastRead = null;
            _hasRead = false;
        }
    }

    private static byte[]? Encode(byte[]? bytes)
    {
        if (bytes is null) return null;
        if (bytes.Length % 2 != 0 || bytes.Length > ushort.MaxValue - 1) throw InvalidSnapshot();
        var result = new byte[bytes.Length + 1];
        result[0] = 1;
        bytes.CopyTo(result, 1);
        return result;
    }

    private static byte[]? Decode(byte[]? bytes)
    {
        if (bytes is null) return null;
        if (bytes.Length == 0 || bytes[0] != 1 || bytes.Length % 2 != 1 || bytes.Length > ushort.MaxValue)
            throw InvalidSnapshot();
        return bytes.AsSpan(1).ToArray();
    }

    private static InvalidDataException InvalidSnapshot() => new("The saved sign-in secret format is invalid.");
    private static void Clear(byte[]? bytes) { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
}

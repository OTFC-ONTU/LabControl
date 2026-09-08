using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabControl.Shared.Setup;

/// <summary>Only fixed Winlogon values and the LSA secret. Null preserves absence.
/// Identity values are REG_SZ; the countdown retains REG_DWORD or REG_SZ type.
/// Unsupported native types are refused.</summary>
public sealed record StudentSignInSnapshot([property: JsonRequired] string? Enabled,
    [property: JsonRequired] string? User, [property: JsonRequired] string? Domain,
    [property: JsonRequired] string? PlaintextPassword, [property: JsonRequired] byte[]? Secret,
    [property: JsonRequired] StudentSignInCount? LogonCount = null)
{
    public bool SameAs(StudentSignInSnapshot other) => Enabled == other.Enabled && User == other.User
        && Domain == other.Domain && PlaintextPassword == other.PlaintextPassword && LogonCount == other.LogonCount
        && (Secret is null ? other.Secret is null : other.Secret is not null && Secret.AsSpan().SequenceEqual(other.Secret));
}

/// <summary>Exact absent-or-REG_DWORD/REG_SZ countdown storage. Do not parse string
/// counts into numbers: removal must restore their original type and contents.</summary>
public sealed record StudentSignInCount([property: JsonRequired] string Kind,
    [property: JsonRequired] string? Text, [property: JsonRequired] uint? Dword)
{
    public void Validate()
    {
        if (!(Kind == "dword" && Dword is not null && Text is null
            || Kind == "string" && Text is not null && Dword is null))
            throw new InvalidDataException("The sign-in countdown snapshot is invalid.");
    }
}

public interface IStudentSignInStore
{
    string? ReadAutoLogonSid();
    void WriteAutoLogonSid(string? expected, string? value);
    StudentSignInSnapshot Read();
    // Disable autologon first, change credentials, enable last. Guard the entire tuple
    // before each write. A partial write must throw; never invent a rollback baseline.
    void Write(StudentSignInSnapshot expected, StudentSignInSnapshot value);
}

/// <summary>One protected ownership record keeps credentials and their enable switch
/// together across repair/removal. Account enablement is a separate final operation.</summary>
public sealed class StudentSignIn(InstallationState state, Func<string?> currentSid,
    Func<SetupSettingsJournal> journal, IStudentSignInStore store, string machineName, Action? reportUntrackedSid = null)
{
    public SettingChangeResult? Apply()
    {
        if (!Enabled()) return null;
        if (string.IsNullOrWhiteSpace(machineName)) throw new InvalidOperationException("A local machine name is required.");
        var settings = journal();
        if (HasLegacySidBaseline(settings)) reportUntrackedSid?.Invoke();
        else
        {
            var result = settings.Apply(new SidSetting(store, RequireAccount), SidSetting.Encode(state.Read()!.CreatedStudentSid));
            if (result == SettingChangeResult.Conflict) return result;
        }
        using var setting = new Setting(store, RequireAccount);
        var desired = new StudentSignInSnapshot("1", Defaults.StudentAccountName, machineName, null,
            Encoding.Unicode.GetBytes(Defaults.StudentDefaultPassword));
        var bytes = Encode(desired);
        try { return settings.Apply(setting, bytes); }
        finally { Clear(desired.Secret); Clear(bytes); }
    }

    public SettingChangeResult? Restore()
    {
        if (!Enabled()) return null;
        var settings = journal();
        if (HasLegacySidBaseline(settings)) reportUntrackedSid?.Invoke();
        using var setting = new Setting(store, RequireAccount);
        var result = settings.Restore(setting);
        if (result == SettingChangeResult.Conflict) return result;
        var sidResult = settings.Restore(new SidSetting(store, RequireAccount));
        return sidResult == SettingChangeResult.Conflict || result == SettingChangeResult.Untracked ? sidResult : result;
    }

    private static bool HasLegacySidBaseline(SetupSettingsJournal settings)
    {
        var ids = settings.SettingIds();
        return ids.Contains("student.autologon-tuple", StringComparer.Ordinal)
            && !ids.Contains("student.autologon-sid", StringComparer.Ordinal);
    }

    private sealed class SidSetting(IStudentSignInStore store, Action guard) : ISetupSetting
    {
        private string? _last;
        private bool _read;
        public string Id => "student.autologon-sid";
        public static byte[]? Encode(string? value) => value is null ? null : Encoding.Unicode.GetBytes("s" + value);
        public byte[]? Read() { guard(); _last = store.ReadAutoLogonSid(); _read = true; return Encode(_last); }
        public void Write(byte[]? value)
        {
            if (!_read) throw new InvalidOperationException("Read sign-in identity before changing it.");
            if (value is not null && (value.Length < 2 || value.Length % 2 != 0 || value[0] != 's' || value[1] != 0))
                throw new InvalidDataException("The protected sign-in identity is invalid.");
            guard();
            store.WriteAutoLogonSid(_last, value is null ? null : Encoding.Unicode.GetString(value.AsSpan(2)));
            _read = false;
        }
    }

    private bool Enabled()
    {
        var configuration = state.Read() ?? throw new InvalidOperationException("Installation history is required for sign-in settings.");
        if (configuration.CreateStudentAccount != true) return false;
        RequireAccount();
        return true;
    }
    private void RequireAccount() => state.RequireManagedStudent(currentSid());
    private static byte[] Encode(StudentSignInSnapshot snapshot)
    {
        if (snapshot.Secret is { Length: > 65534 } || snapshot.Secret?.Length % 2 == 1)
            throw new InvalidDataException("The sign-in secret format is invalid.");
        snapshot.LogonCount?.Validate();
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        try
        {
            var bytes = new byte[json.Length + 1];
            bytes[0] = 1;
            json.CopyTo(bytes, 1);
            return bytes;
        }
        finally { Clear(json); }
    }
    private static StudentSignInSnapshot Decode(byte[]? bytes)
    {
        try
        {
            if (bytes is not { Length: > 1 } || bytes[0] != 1) throw new InvalidDataException();
            var value = JsonSerializer.Deserialize<StudentSignInSnapshot>(bytes.AsSpan(1));
            if (value is null || value.Secret is { Length: > 65534 } || value.Secret?.Length % 2 == 1)
                throw new InvalidDataException();
            value.LogonCount?.Validate();
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { throw new InvalidDataException("The protected sign-in snapshot is invalid."); }
    }
    private static void Clear(byte[]? bytes) { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }

    private sealed class Setting(IStudentSignInStore store, Action guard) : ISetupSetting, IDisposable
    {
        private StudentSignInSnapshot? _last;
        public string Id => "student.autologon-tuple";
        public byte[] Read()
        {
            Dispose();
            guard();
            _last = store.Read();
            return Encode(_last);
        }
        public void Write(byte[]? value)
        {
            var desired = Decode(value);
            try
            {
                guard();
                store.Write(_last ?? throw new InvalidOperationException("Read sign-in settings before changing them."), desired);
            }
            finally { Clear(desired.Secret); Dispose(); }
        }
        public void Dispose() { Clear(_last?.Secret); _last = null; }
    }
}

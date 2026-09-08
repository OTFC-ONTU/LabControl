using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabControl.Shared.Setup;

public enum StudentSessionPolicy { PrivacyExperience, EdgeFirstRun, OneDriveSignInNotifications }
public interface IStudentSessionPolicyStore
{
    uint? Read(StudentSessionPolicy policy);
    void Write(StudentSessionPolicy policy, uint? expected, uint? desired);
}
public sealed class StudentSessionSettings(InstallationState state, Func<string?> currentStudentSid,
    Func<SetupSettingsJournal> journal, IStudentSessionPolicyStore store)
{
    public SettingChangeResult? Apply(StudentSessionPolicy policy)
    {
        if (!Enabled()) return null;
        return journal().Apply(new Setting(policy, store, RequireStudent), MachineRegistrySetting.Encode(1));
    }
    public SettingChangeResult? Restore(StudentSessionPolicy policy)
    {
        if (!Enabled()) return null;
        return journal().Restore(new Setting(policy, store, RequireStudent));
    }
    private bool Enabled()
    {
        var configuration = state.Read() ?? throw new InvalidOperationException("Installation history is required for session setup.");
        if (configuration.CreateStudentAccount != true) return false;
        RequireStudent(); return true;
    }
    private void RequireStudent() => state.RequireManagedStudent(currentStudentSid());
    private sealed class Setting(StudentSessionPolicy policy, IStudentSessionPolicyStore store, Action guard) : ISetupSetting
    {
        private uint? _last; private bool _read;
        public string Id => policy switch
        {
            StudentSessionPolicy.PrivacyExperience => "student.session-privacy",
            StudentSessionPolicy.EdgeFirstRun => "student.session-edge",
            StudentSessionPolicy.OneDriveSignInNotifications => "student.session-onedrive-notifications",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
        public byte[]? Read() { _read = false; guard(); _last = store.Read(policy); _read = true; return MachineRegistrySetting.Encode(_last); }
        public void Write(byte[]? value)
        {
            if (!_read) throw new InvalidOperationException("Read session policy before changing it.");
            _read = false;
            uint? desired = null;
            if (value is not null)
            {
                if (value.Length != 5 || value[0] != 4) throw new InvalidDataException("The saved session policy type is invalid.");
                desired = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(1));
            }
            guard(); store.Write(policy, _last, desired);
        }
    }
}

public sealed record AdministratorVisibilitySnapshot([property: JsonRequired] string AccountName,
    [property: JsonRequired] uint? Value);
public interface IAdministratorVisibilityStore
{
    string CurrentLocalAdministratorSid();
    uint? ReadCredentialPrompt(string administratorSid);
    void WriteCredentialPrompt(string administratorSid, uint? expected, uint? desired);
    AdministratorVisibilitySnapshot Read(string sid);
    void Write(string sid, AdministratorVisibilitySnapshot expected, AdministratorVisibilitySnapshot desired);
}
public sealed class AdministratorVisibility(InstallationState state, Func<string?> currentStudentSid,
    Func<SetupSettingsJournal> journal, IAdministratorVisibilityStore store)
{
    public SettingChangeResult? Apply()
    {
        if (!Enabled()) return null;
        var sid = state.Read()!.HiddenAdministratorSid ?? state.RecordHiddenAdministrator(store.CurrentLocalAdministratorSid());
        var prompt = journal().Apply(new CredentialPromptSetting(sid, store, RequireStudent), MachineRegistrySetting.Encode(0));
        if (prompt == SettingChangeResult.Conflict) return prompt;
        return journal().ApplyFromCurrent(new Setting(sid, store, RequireStudent), current =>
        {
            var original = Decode(current);
            return Encode(original with { Value = 0 });
        });
    }
    public SettingChangeResult? Restore()
    {
        var configuration = state.Read() ?? throw new InvalidOperationException("Installation history is required for account visibility.");
        if (configuration.CreateStudentAccount != true) return null;
        var sid = configuration.HiddenAdministratorSid;
        if (sid is null) return SettingChangeResult.Untracked;
        // Restoring the recorded administrator's tile never operates on the student.
        // A deleted or replaced student must not prevent this recovery path.
        void RequireRecordedAdministrator()
        {
            var current = state.Read();
            if (current?.CreateStudentAccount != true || current.CreatedStudentSid is null
                || current.HiddenAdministratorSid != sid)
                throw new InvalidOperationException("The recorded administrator visibility ownership changed.");
        }
        var visibility = journal().Restore(new Setting(sid, store, RequireRecordedAdministrator));
        if (visibility == SettingChangeResult.Conflict) return visibility;
        // Keep explicit credential entry while an unchanged hidden tile is restored.
        // Only then restore the machine-wide UAC enumeration baseline.
        var prompt = journal().Restore(new CredentialPromptSetting(sid, store, RequireRecordedAdministrator));
        return prompt == SettingChangeResult.Conflict || visibility == SettingChangeResult.Untracked ? prompt : visibility;
    }
    private bool Enabled()
    {
        var configuration = state.Read() ?? throw new InvalidOperationException("Installation history is required for account visibility.");
        if (configuration.CreateStudentAccount != true) return false;
        RequireStudent(); return true;
    }
    private void RequireStudent() => state.RequireManagedStudent(currentStudentSid());
    private static byte[] Encode(AdministratorVisibilitySnapshot value) => [1, .. JsonSerializer.SerializeToUtf8Bytes(value)];
    private static AdministratorVisibilitySnapshot Decode(byte[]? bytes)
    {
        try
        {
            if (bytes is not { Length: > 1 } || bytes[0] != 1) throw new InvalidDataException();
            var value = JsonSerializer.Deserialize<AdministratorVisibilitySnapshot>(bytes.AsSpan(1));
            if (value is null || string.IsNullOrWhiteSpace(value.AccountName)) throw new InvalidDataException();
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { throw new InvalidDataException("The saved account visibility is invalid."); }
    }
    private sealed class CredentialPromptSetting(string sid, IAdministratorVisibilityStore store, Action guard) : ISetupSetting
    {
        private uint? _last;
        private bool _read;
        public string Id => "student.admin-credential-prompt";
        public byte[]? Read()
        {
            _read = false; guard();
            _last = store.ReadCredentialPrompt(sid); _read = true;
            return MachineRegistrySetting.Encode(_last);
        }
        public void Write(byte[]? value)
        {
            if (!_read) throw new InvalidOperationException("Read administrator credential prompting before changing it.");
            _read = false;
            uint? desired = null;
            if (value is not null)
            {
                if (value.Length != 5 || value[0] != 4) throw new InvalidDataException("The saved administrator prompting type is invalid.");
                desired = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(1));
            }
            guard(); store.WriteCredentialPrompt(sid, _last, desired);
        }
    }

    private sealed class Setting(string sid, IAdministratorVisibilityStore store, Action guard) : ISetupSetting
    {
        private AdministratorVisibilitySnapshot? _last;
        public string Id => "student.admin-visibility";
        public byte[] Read() { _last = null; guard(); _last = store.Read(sid); return Encode(_last); }
        public void Write(byte[]? bytes)
        {
            var desired = Decode(bytes);
            var expected = _last ?? throw new InvalidOperationException("Read account visibility before changing it.");
            _last = null;
            if (desired.AccountName != expected.AccountName) throw new InvalidOperationException("The administrator account name changed.");
            guard(); store.Write(sid, expected, desired);
        }
    }
}

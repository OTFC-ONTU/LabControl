using System.Text;

namespace LabControl.Shared.Setup;

public sealed record HostnameState(string Active, string Pending)
{
    public bool RebootRequired => !string.Equals(Active, Pending, StringComparison.OrdinalIgnoreCase);
}

public interface IHostnameSystem
{
    HostnameState Read();
    void SetPending(string expectedPending, string desired);
}

/// <summary>Journal the durable pending name; the active name changes at reboot and
/// must not turn our own completed rename into a later-edit conflict.</summary>
public sealed class HostnameSetting(IHostnameSystem system) : ISetupSetting
{
    private string? _last;
    public string Id => "machine.hostname";
    public SetupCheck Check(SetupSettingsJournal journal, string desired) => journal.Check(this, _ => Encode(Normalize(desired)));
    public SettingChangeResult Apply(SetupSettingsJournal journal, string desired)
    {
        desired = Normalize(desired);
        return journal.ApplyFromCurrent(this, current =>
        {
            var observed = system.Read();
            if (!Equal(Decode(current), Normalize(observed.Pending))) throw new IOException("The pending hostname changed during setup.");
            if (observed.RebootRequired && !Equal(Normalize(observed.Pending), desired))
                throw new InvalidOperationException("Another hostname change is awaiting reboot; preserve it and restart before setup.");
            return Encode(desired);
        });
    }
    public HostnameState Status() => system.Read();
    public byte[] Read()
    {
        _last = null;
        _last = Normalize(system.Read().Pending);
        return Encode(_last);
    }
    public void Write(byte[]? value)
    {
        var desired = Decode(value);
        var expected = _last ?? throw new InvalidOperationException("Read the hostname before changing it.");
        _last = null;
        system.SetPending(expected, desired);
    }
    public static string Normalize(string value)
    {
        // One interoperable DNS/NetBIOS label. Refuse unusual legacy names rather than
        // promise a round trip that SetComputerNameEx cannot preserve.
        if (string.IsNullOrWhiteSpace(value) || value.Length > 15 || value[0] == '-' || value[^1] == '-'
            || value.All(char.IsAsciiDigit) || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new InvalidDataException("The hostname is not a supported DNS/NetBIOS label.");
        return value.ToUpperInvariant();
    }
    private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    private static byte[] Encode(string value) => [1, .. Encoding.ASCII.GetBytes(value)];
    private static string Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 1 and <= 16 } || bytes[0] != 1 || bytes.AsSpan(1).ContainsAnyExceptInRange((byte)'-', (byte)'Z'))
            throw new InvalidDataException("The saved hostname is invalid.");
        return Normalize(Encoding.ASCII.GetString(bytes, 1, bytes.Length - 1));
    }
}

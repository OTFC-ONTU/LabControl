using System.Globalization;
using System.Text.Json;
using LabControl.Shared;
using LabControl.Shared.Persistence;

namespace LabControl.Console.Services;

public enum EventSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>One line in the console's event panel, and one line in <c>logs/events-&lt;day&gt;.jsonl</c>.</summary>
public sealed record EventRecord(
    long AtUnix,
    EventSeverity Severity,
    string Code,
    string Message,
    string? AgentId = null,
    int Number = 0)
{
    public DateTimeOffset At => DateTimeOffset.FromUnixTimeSeconds(AtUnix);
}

/// <summary>
/// Everything the teacher might want to know happened: enrolments, refusals, reinstalls,
/// revocations, agent-reported errors. Kept in memory for the panel and appended to a
/// per-day file so that what a PC said last week can still be found.
/// </summary>
public sealed class EventLog
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<EventRecord> _recent = [];
    private readonly Lock _gate = new();

    /// <summary>How many events the panel keeps in memory; the files keep everything.</summary>
    private const int RecentCapacity = 2000;

    public EventLog(string directory, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action<EventRecord>? Added;

    public IReadOnlyList<EventRecord> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    public EventRecord Info(string code, string message, string? agentId = null, int number = 0) =>
        Add(EventSeverity.Info, code, message, agentId, number);

    public EventRecord Warning(string code, string message, string? agentId = null, int number = 0) =>
        Add(EventSeverity.Warning, code, message, agentId, number);

    public EventRecord Error(string code, string message, string? agentId = null, int number = 0) =>
        Add(EventSeverity.Error, code, message, agentId, number);

    public EventRecord Add(EventSeverity severity, string code, string message, string? agentId = null, int number = 0)
    {
        var record = new EventRecord(_clock().ToUnixTimeSeconds(), severity, code, message, agentId, number);

        lock (_gate)
        {
            _recent.Add(record);
            if (_recent.Count > RecentCapacity)
            {
                _recent.RemoveRange(0, _recent.Count - RecentCapacity);
            }

            Append(record);
        }

        Added?.Invoke(record);
        return record;
    }

    private void Append(EventRecord record)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var file = Path.Combine(_directory, string.Format(CultureInfo.InvariantCulture, Defaults.EventLogFilePattern, record.At.ToLocalTime()));
            File.AppendAllText(file, JsonSerializer.Serialize(record, JsonStore.Options.WithoutIndent()) + Environment.NewLine);
        }
        catch (IOException)
        {
            // A full disk must not take the console down; the panel still has the event.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class JsonOptionsExtensions
{
    private static JsonSerializerOptions? _compact;

    /// <summary>The store's options, one record per line.</summary>
    public static JsonSerializerOptions WithoutIndent(this JsonSerializerOptions options) =>
        _compact ??= new JsonSerializerOptions(options) { WriteIndented = false };
}

using LabControl.Shared.Persistence;
using LabControl.Shared.Video.H264;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>
/// The teacher's last *Text / Video* choices (M7 portion 6, D-91, owner decisions 2026-10-27):
/// one for *Show my screen*, one for the full-view window, kept in <c>preferences.json</c> on
/// this computer — never in a backup or lab file — and *Text* until chosen. A choice that
/// cannot be saved still holds for this run. Without a data directory (the tests) nothing is
/// written.
/// </summary>
public sealed class VideoPreferences
{
    private readonly string? _dataDirectory;
    private readonly ILogger? _log;
    private readonly Lock _lock = new();
    private bool _broadcastVideo;
    private bool _fullViewVideo;

    public VideoPreferences(string? dataDirectory = null, ILogger? log = null)
    {
        _dataDirectory = dataDirectory;
        _log = log;
        if (dataDirectory is null)
        {
            return;
        }

        try
        {
            var document = JsonStore.LoadIfExists<PreferencesDocument>(ConsoleLanguage.PreferencesPath(dataDirectory), PreferencesDocument.Migrations);
            _broadcastVideo = document?.BroadcastVideo ?? false;
            _fullViewVideo = document?.FullViewVideo ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SchemaVersionException or System.Text.Json.JsonException)
        {
            _log?.LogWarning("preferences.json could not be read, Text is the choice: {Message}", ex.Message);
        }
    }

    /// <summary>This console can encode H.264 for the broadcast; on Linux it cannot.</summary>
    public static bool CanBroadcastVideo => H264Codecs.CanEncode;

    /// <summary>This console can decode H.264 for a full view.</summary>
    public static bool CanWatchVideo => H264Codecs.CanDecode;

    public bool BroadcastVideo
    {
        get
        {
            lock (_lock)
            {
                return _broadcastVideo;
            }
        }
        set
        {
            lock (_lock)
            {
                if (_broadcastVideo == value)
                {
                    return;
                }

                _broadcastVideo = value;
            }

            Save(document => document.BroadcastVideo = value);
        }
    }

    public bool FullViewVideo
    {
        get
        {
            lock (_lock)
            {
                return _fullViewVideo;
            }
        }
        set
        {
            lock (_lock)
            {
                if (_fullViewVideo == value)
                {
                    return;
                }

                _fullViewVideo = value;
            }

            Save(document => document.FullViewVideo = value);
        }
    }

    private void Save(Action<PreferencesDocument> change)
    {
        if (_dataDirectory is null)
        {
            return;
        }

        var path = ConsoleLanguage.PreferencesPath(_dataDirectory);
        try
        {
            PreferencesDocument document;
            try
            {
                document = JsonStore.LoadIfExists<PreferencesDocument>(path, PreferencesDocument.Migrations) ?? new PreferencesDocument();
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                document = new PreferencesDocument();
            }

            change(document);
            JsonStore.Save(path, document, PreferencesDocument.Migrations);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SchemaVersionException)
        {
            // The choice holds for this run; the teacher is not interrupted for a preference.
            _log?.LogWarning("the Text/Video choice could not be saved: {Message}", ex.Message);
        }
    }
}

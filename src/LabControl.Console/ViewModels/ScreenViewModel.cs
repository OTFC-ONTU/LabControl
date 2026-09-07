using CommunityToolkit.Mvvm.ComponentModel;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;

namespace LabControl.Console.ViewModels;

/// <summary>
/// The single-PC view (ARCHITECTURE §8, M3): the native-resolution picture of one PC while
/// its window is open. Opening it switches the PC to full mode, closing it switches back;
/// until the first full frame the thumbnail is shown scaled up so the window is never
/// blank. Input control is portion 3.
/// </summary>
public sealed partial class ScreenViewModel : ObservableObject
{
    private readonly LabSession _session;
    private readonly Action<Action> _post;
    private int _pending;

    public ScreenViewModel(LabSession session, MachineTileViewModel tile, Action<Action> post)
    {
        _session = session;
        _post = post;
        AgentId = tile.AgentId;
        Number = tile.Number;
        Screen = session.Screens.Get(tile.AgentId);
        Title = Strings.Format("Screen.Title", Name);
    }

    public string AgentId { get; }

    public int Number { get; }

    public string Name => string.Format(Strings.Culture, Defaults.MachineNameFormat, Number);

    public string Title { get; }

    public AgentScreen Screen { get; }

    /// <summary>The picture to draw: the full one once it exists, the thumbnail until then.</summary>
    public ScreenImage Image => Screen.Full.HasFrame ? Screen.Full : Screen.Thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Image))]
    public partial long FrameVersion { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStale { get; set; }

    /// <summary>Raised when the teacher opens the same PC again while this window is up: bring it to the front.</summary>
    public event Action? Activated;

    public void Activate() => Activated?.Invoke();

    /// <summary>The window opened: ask the PC for its full picture and start listening.</summary>
    public void Open()
    {
        _session.Screens.Updated += OnUpdated;
        _session.SetScreenMode(AgentId, VideoMode.Full);
        Tick();
    }

    /// <summary>The window closed; the main view model forgets it.</summary>
    public event Action? Closed;

    /// <summary>The window closed: back to the thumbnail.</summary>
    public void Close()
    {
        _session.Screens.Updated -= OnUpdated;
        _session.SetScreenMode(AgentId, VideoMode.Thumbnail);
        Screen.Full.Clear();
        Closed?.Invoke();
    }

    private void OnUpdated(AgentScreen screen, FrameOutcome outcome)
    {
        if (!string.Equals(screen.AgentId, AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // One UI hop per burst, not per frame: full mode can deliver twenty a second.
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _post(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                FrameVersion++;
            });
        }
    }

    /// <summary>Once a second from the window: the numbers under the picture.</summary>
    public void Tick()
    {
        var now = _session.Now;
        var linked = _session.IsLinked(AgentId);
        Screen.Trim(now);
        IsStale = !linked || Screen.IsStalled(now, linked);

        if (!linked)
        {
            Status = Strings.Format("Screen.Offline", Name);
        }
        else if (!Screen.Full.HasFrame)
        {
            Status = Strings.Format("Screen.Waiting", Name);
        }
        else
        {
            Status = Strings.Format("Screen.Stats", Screen.Full.ScreenWidth, Screen.Full.ScreenHeight, Screen.FramesPerSecond, Screen.BytesPerSecond * 8 / 1024.0);
        }
    }
}

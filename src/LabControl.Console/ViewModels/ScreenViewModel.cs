using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Control;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;

namespace LabControl.Console.ViewModels;

/// <summary>
/// The single-PC view (ARCHITECTURE §8, M3): the native-resolution picture of one PC while
/// its window is open. Opening it switches the PC to full mode, closing it switches back;
/// until the first full frame the thumbnail is shown scaled up so the window is never
/// blank. With <see cref="IsControlling"/> on, the teacher's mouse and keyboard go to the
/// PC (portion 3, D-36): the window feeds events to the <see cref="InputMapper"/>, the
/// <see cref="InputQueue"/> holds the latest mouse move until the window's flush timer, and
/// everything pressed is released when control ends, the window loses focus or closes.
/// </summary>
public sealed partial class ScreenViewModel : ObservableObject
{
    private readonly LabSession _session;
    private readonly Action<Action> _post;
    private readonly InputQueue _queue = new();
    private int _pending;

    public ScreenViewModel(LabSession session, MachineTileViewModel tile, Action<Action> post, bool? macOs = null)
    {
        _session = session;
        _post = post;
        AgentId = tile.AgentId;
        Number = tile.Number;
        Screen = session.Screens.Get(tile.AgentId);
        Title = Strings.Format("Screen.Title", Name);
        Mapper = new InputMapper(macOs);
        ControlHint = Strings.Format(Mapper.CommandIsControl ? "Screen.ControlHint" : "Screen.ControlHintWindows", Name);
    }

    public string AgentId { get; }

    public int Number { get; }

    public string Name => string.Format(Strings.Culture, Defaults.MachineNameFormat, Number);

    public string Title { get; }

    public AgentScreen Screen { get; }

    /// <summary>Window events in, <c>Input</c> messages out; the window calls it, this sends.</summary>
    public InputMapper Mapper { get; }

    public string ControlHint { get; }

    /// <summary>The picture to draw: the full one once it exists, the thumbnail until then.</summary>
    public ScreenImage Image => Screen.Full.HasFrame ? Screen.Full : Screen.Thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Image))]
    public partial long FrameVersion { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStale { get; set; }

    /// <summary>The teacher's mouse and keyboard go to the PC. Turned off by itself when the PC cannot take them.</summary>
    [ObservableProperty]
    public partial bool IsControlling { get; set; }

    /// <summary>The PC is linked, current, and has a helper in a session: control can be switched on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCtrlAltDelCommand), nameof(SendWindowsKeyCommand))]
    public partial bool CanControl { get; set; }

    /// <summary>Why control is unavailable, for the status line; empty when it is available.</summary>
    [ObservableProperty]
    public partial string ControlProblem { get; set; } = string.Empty;

    /// <summary>Messages sent to the PC since the window opened, for the tests and a debug eye.</summary>
    public long InputsSent { get; private set; }

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

    /// <summary>The window closed: release what is held, back to the thumbnail.</summary>
    public void Close()
    {
        ReleaseAll();
        IsControlling = false;
        _session.Screens.Updated -= OnUpdated;
        _session.SetScreenMode(AgentId, VideoMode.Thumbnail);
        Screen.Full.Clear();
        Closed?.Invoke();
    }

    // ------------------------------------------------------------------ input (portion 3)

    partial void OnIsControllingChanged(bool value)
    {
        if (!value)
        {
            ReleaseAll();
        }
    }

    /// <summary>One message from the window: held if it is a mouse move, sent otherwise.</summary>
    public void Send(Input? input)
    {
        if (input is null || !IsControlling)
        {
            return;
        }

        foreach (var message in _queue.Offer(input))
        {
            Deliver(message);
        }
    }

    public void Send(IEnumerable<Input> inputs)
    {
        foreach (var input in inputs)
        {
            Send(input);
        }
    }

    /// <summary>From the window's timer while control is on: the latest mouse move goes out.</summary>
    public void FlushInput()
    {
        if (_queue.Flush() is { } held && IsControlling)
        {
            Deliver(held);
        }
    }

    /// <summary>The window lost focus: keys the platform will never report released are released now.</summary>
    public void ReleaseAll()
    {
        foreach (var message in Mapper.ReleaseAll())
        {
            Deliver(message);
        }

        _queue.Flush();
    }

    [RelayCommand(CanExecute = nameof(CanControl))]
    private void SendCtrlAltDel() => Deliver(InputMessages.CtrlAltDel());

    [RelayCommand(CanExecute = nameof(CanControl))]
    private void SendWindowsKey()
    {
        foreach (var message in InputMapper.WindowsKeyTap())
        {
            Deliver(message);
        }
    }

    private void Deliver(Input input)
    {
        if (_session.SendInput(AgentId, input))
        {
            InputsSent++;
        }
    }

    // ------------------------------------------------------------------ the picture

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

    /// <summary>Once a second from the window: the numbers under the picture, and whether control is possible.</summary>
    public void Tick()
    {
        var now = _session.Now;
        var connection = _session.FindLinked(AgentId);
        var linked = connection is not null;
        Screen.Trim(now);
        IsStale = !linked || Screen.IsStalled(now, linked);

        ControlProblem = connection switch
        {
            null => Strings.Format("Screen.Offline", Name),
            { IsOutdated: true } => Strings.Get("Screen.OutdatedAgent"),
            { NoSession: true } => Strings.Get("Screen.NoSession"),
            { HelperAlive: false } => Strings.Get("Screen.NoHelper"),
            _ => string.Empty,
        };
        CanControl = ControlProblem.Length == 0;
        if (!CanControl && IsControlling)
        {
            IsControlling = false;
        }

        if (connection is null)
        {
            Status = Strings.Format("Screen.Offline", Name);
        }
        else if (!CanControl)
        {
            Status = Strings.Format("Screen.CannotControl", Name, ControlProblem);
        }
        else if (connection.InputProblem is { } inputProblem && IsControlling)
        {
            Status = Strings.Format("Screen.InputProblem", Name, InputReasonText(inputProblem));
        }
        else if (connection.CaptureProblem is { } captureProblem)
        {
            Status = Strings.Format("Screen.CaptureProblem", Name, MachineTileViewModel.CaptureReasonText(captureProblem));
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

    private static string InputReasonText(string reason) =>
        reason is "no_desktop" or "blocked" or "failed" ? Strings.Get("Input." + reason) : reason.Replace('_', ' ');
}

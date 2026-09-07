using CommunityToolkit.Mvvm.ComponentModel;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Video;

namespace LabControl.Console.ViewModels;

public enum TileStatus
{
    Offline = 0,
    Online = 1,

    /// <summary>Linked, but below the console's minimum protocol version (D-19).</summary>
    Outdated = 2,

    /// <summary>Not linked here while another console is live: presumed held by it (§3.7.2).</summary>
    HeldElsewhere = 3,
}

/// <summary>One PC in the lab view. Refreshed in place from the machine record and the live link.</summary>
public sealed partial class MachineTileViewModel : ObservableObject
{
    public MachineTileViewModel(MachineRecord machine, AgentScreen screen)
    {
        AgentId = machine.AgentId;
        Number = machine.Number;
        Screen = screen;
    }

    public string AgentId { get; }

    /// <summary>This PC's pictures (M3); the tile draws the thumbnail.</summary>
    public AgentScreen Screen { get; }

    public ScreenImage Thumbnail => Screen.Thumbnail;

    /// <summary>Bumped on the UI thread for every thumbnail that arrived; the tile redraws on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    public partial long FrameVersion { get; set; }

    public bool HasPicture => Screen.Thumbnail.HasFrame;

    /// <summary>The picture is history: the PC is offline or has stopped sending.</summary>
    [ObservableProperty]
    public partial bool IsPictureStale { get; set; }

    /// <summary>"no picture" / "picture stalled" — why a linked tile shows nothing live (graceful degradation, ROADMAP M3).</summary>
    [ObservableProperty]
    public partial string PictureNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Number { get; set; }

    public string Name => string.Format(Strings.Culture, Defaults.MachineNameFormat, Number);

    [ObservableProperty]
    public partial string Hostname { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionText))]
    public partial string LoggedOnUser { get; set; } = string.Empty;

    /// <summary>The lock screen is up, from the agent's last <c>SessionState</c> (M2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionText))]
    public partial bool SessionLocked { get; set; }

    /// <summary>The agent says its session helper is not running — screens and control will not work there.</summary>
    [ObservableProperty]
    public partial bool HelperDown { get; set; }

    /// <summary>"student", "student (locked)", "nobody logged on" — the tile's session line.</summary>
    public string SessionText =>
        LoggedOnUser.Length == 0
            ? (Status is TileStatus.Online or TileStatus.Outdated ? Strings.Get("Tile.NobodyLoggedOn") : string.Empty)
            : SessionLocked ? Strings.Format("Tile.UserLocked", LoggedOnUser) : LoggedOnUser;

    [ObservableProperty]
    public partial string AgentVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CertificateSerial { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastSeen { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsOnline), nameof(SessionText))]
    public partial TileStatus Status { get; set; }

    /// <summary>Which console holds the PC when it is not this one, for the tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string HeldBy { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NeedsRenewal { get; set; }

    /// <summary>A magic packet went out and the PC has not linked yet (ARCHITECTURE §6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsWaking { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // Layout: cell and pixel position; the view sizes the cells.

    [ObservableProperty]
    public partial int Column { get; set; }

    [ObservableProperty]
    public partial int Row { get; set; }

    [ObservableProperty]
    public partial double X { get; set; }

    [ObservableProperty]
    public partial double Y { get; set; }

    public bool IsOnline => Status is TileStatus.Online or TileStatus.Outdated;

    public string StatusText => Status switch
    {
        TileStatus.Online => Strings.Get("Tile.Online"),
        TileStatus.Outdated => Strings.Get("Tile.Outdated"),
        TileStatus.HeldElsewhere => Strings.Format("Tile.HeldBy", HeldBy),
        _ => IsWaking ? Strings.Get("Tile.Waking") : Strings.Get("Tile.Offline"),
    };

    /// <summary>The clock-driven part: whether the picture is live, from the store's timestamps.</summary>
    public void RefreshPicture(DateTimeOffset now, bool linked)
    {
        Screen.Trim(now);
        var stalled = Screen.IsStalled(now, linked);
        IsPictureStale = !linked || stalled;
        PictureNote = !linked || HelperDown
            ? string.Empty
            : stalled
                ? Strings.Get(HasPicture ? "Tile.PictureStalled" : "Tile.NoPicture")
                : string.Empty;
        if (HasPicture != _hadPicture)
        {
            _hadPicture = HasPicture;
            OnPropertyChanged(nameof(HasPicture));
        }
    }

    private bool _hadPicture;

    public void Refresh(MachineRecord machine, AgentConnection? connection, string? heldBy, DateTimeOffset now, bool waking = false)
    {
        IsWaking = waking && connection is null;
        Number = machine.Number;
        OnPropertyChanged(nameof(Name));
        Hostname = machine.Hostname;
        LoggedOnUser = machine.LoggedOnUser ?? string.Empty;
        AgentVersion = machine.AgentVersion ?? string.Empty;
        Address = machine.LastIp ?? string.Empty;
        CertificateSerial = machine.CertificateSerial;
        NeedsRenewal = machine.CertificateNotAfterUnix > 0 &&
                       DateTimeOffset.FromUnixTimeSeconds(machine.CertificateNotAfterUnix) - now < Defaults.CertificateRenewalLeadTime;

        SessionLocked = connection?.SessionLocked ?? false;
        HelperDown = connection?.HelperAlive == false;

        if (connection is not null)
        {
            Status = connection.IsOutdated ? TileStatus.Outdated : TileStatus.Online;
            HeldBy = string.Empty;
            LastSeen = Strings.Get("Tile.Now");
        }
        else
        {
            HeldBy = heldBy ?? string.Empty;
            Status = heldBy is null ? TileStatus.Offline : TileStatus.HeldElsewhere;
            LastSeen = machine.LastSeenUnix == 0
                ? Strings.Get("Tile.Never")
                : DateTimeOffset.FromUnixTimeSeconds(machine.LastSeenUnix).ToLocalTime().ToString("g", Strings.Culture);
        }

        RefreshPicture(now, connection is not null);
    }
}

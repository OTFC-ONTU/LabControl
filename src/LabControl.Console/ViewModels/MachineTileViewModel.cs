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

    /// <summary>The agent says there is no interactive session at all (M3 portion 3): nothing to capture or drive.</summary>
    [ObservableProperty]
    public partial bool NoSession { get; set; }

    /// <summary>The helper's <c>capture.&lt;reason&gt;</c> code while the screen cannot be captured; empty otherwise.</summary>
    [ObservableProperty]
    public partial string CaptureProblem { get; set; } = string.Empty;

    /// <summary>"student", "student (locked)", "nobody logged on" — the tile's session line.</summary>
    public string SessionText =>
        LoggedOnUser.Length == 0
            ? (Status is TileStatus.Online or TileStatus.Outdated ? Strings.Get("Tile.NobodyLoggedOn") : string.Empty)
            : SessionLocked ? Strings.Format("Tile.UserLocked", LoggedOnUser) : LoggedOnUser;

    [ObservableProperty]
    public partial string AgentVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReadinessNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReadinessAttention { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UpdateNeedsAttention { get; set; }

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

    /// <summary>
    /// The clock-driven part: whether the picture is live, from the store's timestamps, and
    /// the one line that says why there is nothing live (graceful degradation, ROADMAP M3):
    /// the reason the PC gave — no session, helper down, capture failed — before the plain
    /// "stalled" the console works out for itself.
    /// </summary>
    public void RefreshPicture(DateTimeOffset now, bool linked)
    {
        Screen.Trim(now);
        var stalled = Screen.IsStalled(now, linked);
        IsPictureStale = !linked || stalled;
        PictureNote = !linked
            ? string.Empty
            : NoSession
                ? Strings.Get("Tile.NoSession")
                : HelperDown
                    ? Strings.Get("Tile.HelperDown")
                    : CaptureProblem.Length > 0
                        ? Strings.Format("Tile.CaptureProblem", CaptureReasonText(CaptureProblem))
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

    /// <summary>A known capture reason in words; an unknown one as the code itself, so a new reason is never hidden.</summary>
    public static string CaptureReasonText(string reason) =>
        reason is "no_desktop" or "access_lost" or "no_duplication" or "gdi_failed" or "open_failed" or "failed"
            ? Strings.Get("Capture." + reason)
            : reason.Replace('_', ' ');

    public void Refresh(MachineRecord machine, AgentConnection? connection, string? heldBy, DateTimeOffset now, bool waking = false)
    {
        IsWaking = waking && connection is null;
        Number = machine.Number;
        OnPropertyChanged(nameof(Name));
        Hostname = machine.Hostname;
        LoggedOnUser = machine.LoggedOnUser ?? string.Empty;
        AgentVersion = machine.AgentVersion ?? string.Empty;
        var readiness = (machine.SetupReadinessCodes ?? []).Where(LabControl.Shared.Setup.SetupReadiness.IsKnown).Distinct().ToArray();
        ReadinessNote = string.Join("\n", readiness.Select(code => Strings.Get("Readiness." + code)));
        ReadinessAttention = string.Join("\n", readiness.Where(LabControl.Shared.Setup.SetupReadiness.NeedsAttention)
            .Select(code => Strings.Get("Readiness." + code)));
        if (connection is not null)
        {
            var update = connection.UpdateState;
            UpdateNeedsAttention = update.Phase is LabControl.Shared.Protocol.UpdateState.Types.Phase.OnProbation
                or LabControl.Shared.Protocol.UpdateState.Types.Phase.RolledBack;
            UpdateNote = update.Phase switch
            {
                LabControl.Shared.Protocol.UpdateState.Types.Phase.Stable => Strings.Get("Tile.UpdateStable"),
                LabControl.Shared.Protocol.UpdateState.Types.Phase.OnProbation => Strings.Get("Tile.UpdateProbation"),
                LabControl.Shared.Protocol.UpdateState.Types.Phase.RolledBack => Strings.Format("Tile.UpdateRolledBack", update.FailedVersion),
                _ => string.Empty,
            };
        }
        Address = machine.LastIp ?? string.Empty;
        CertificateSerial = machine.CertificateSerial;
        NeedsRenewal = machine.CertificateNotAfterUnix > 0 &&
                       DateTimeOffset.FromUnixTimeSeconds(machine.CertificateNotAfterUnix) - now < Defaults.CertificateRenewalLeadTime;

        SessionLocked = connection?.SessionLocked ?? false;
        NoSession = connection?.NoSession ?? false;
        HelperDown = connection?.HelperAlive == false && !NoSession;
        CaptureProblem = connection?.CaptureProblem ?? string.Empty;

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

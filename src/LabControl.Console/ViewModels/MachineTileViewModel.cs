using CommunityToolkit.Mvvm.ComponentModel;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using LabControl.Shared.Persistence;
using LabControl.Shared.Video;

namespace LabControl.Console.ViewModels;

public enum TileStatus
{
    Offline = 0,
    Online = 1,

    /// <summary>Linked, but below the console's minimum protocol version (D-19).</summary>
    Outdated = 2,

    /// <summary>Positively observed with another teacher machine that is still live (§3.7.2, D-58).</summary>
    HeldElsewhere = 3,

    /// <summary>
    /// Another console is running this lab and this one does not know where the PC is
    /// (M5 §4.6, D-58). The honest middle state: it is not claimed for anybody.
    /// </summary>
    NotSeen = 4,
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

    /// <summary>
    /// The PC holds a lock screen — its own word, from its last <c>OverlayState</c> (M6
    /// portion 1, D-70), never what this console asked for. The badge is fed by this.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockText))]
    public partial bool IsLocked { get; set; }

    /// <summary>Who set the lock: "this console" or the other console's name; empty while unlocked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockText))]
    public partial string LockedBy { get; set; } = string.Empty;

    /// <summary>The lock is up but weaker than usual: the PC's <c>OverlayState.reason</c> (hooks not in place, fallback in use); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockText))]
    public partial string LockReason { get; set; } = string.Empty;

    /// <summary>
    /// The PC shows the teacher's screen — its own word, from <c>OverlayState</c> (M6 portion 2,
    /// D-71). A lock over a broadcast reads as a lock; the broadcast badge comes back when it lifts.
    /// </summary>
    [ObservableProperty]
    public partial bool IsBroadcasting { get; set; }

    /// <summary>Why the PC refused or could not apply the last lock or unlock; empty otherwise.</summary>
    [ObservableProperty]
    public partial string LockProblem { get; set; } = string.Empty;

    /// <summary>
    /// The internet policy the PC enforces — its own word, from <c>InternetState</c> (M6 portion
    /// 3, D-73): <c>OPEN</c> for none, or while this console cannot see the PC.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInternetPolicy), nameof(InternetBadge))]
    public partial InternetPolicy.Types.Mode InternetMode { get; set; } = InternetPolicy.Types.Mode.Open;

    /// <summary>The preset the whitelist came from, for the badge; empty for a block or an unnamed list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InternetBadge))]
    public partial string InternetPreset { get; set; } = string.Empty;

    /// <summary>"Internet blocked by this console, 42 min left", with the PC's reasons; empty while open.</summary>
    [ObservableProperty]
    public partial string InternetText { get; set; } = string.Empty;

    /// <summary>Why the PC refused or could not apply the last internet policy, or why it is not proven open; empty otherwise.</summary>
    [ObservableProperty]
    public partial string InternetProblem { get; set; } = string.Empty;

    public bool HasInternetPolicy => InternetMode != InternetPolicy.Types.Mode.Open;

    /// <summary>"NO INTERNET", "SITES: Java docs", "SITES ONLY".</summary>
    public string InternetBadge => InternetMode switch
    {
        InternetPolicy.Types.Mode.Blocked => Strings.Get("Tile.InternetBlockedBadge"),
        InternetPolicy.Types.Mode.Whitelist when InternetPreset.Length > 0 => Strings.Format("Tile.InternetWhitelistBadgeNamed", InternetPreset),
        InternetPolicy.Types.Mode.Whitelist => Strings.Get("Tile.InternetWhitelistBadge"),
        _ => string.Empty,
    };

    /// <summary>The PC holds an exam — its own word, from <c>ExamState</c> (M6 portion 4, D-75).</summary>
    [ObservableProperty]
    public partial bool InExam { get; set; }

    /// <summary>"EXAM 42:17 · Practical (IntelliJ IDEA)", counted down from the PC's <c>remaining_seconds</c> on this console's clock; "EXAM" for one without a timer.</summary>
    [ObservableProperty]
    public partial string ExamBadge { get; set; } = string.Empty;

    /// <summary>Who set the exam, its programs, what the whitelist closed lately, and the PC's reasons; empty without an exam.</summary>
    [ObservableProperty]
    public partial string ExamText { get; set; } = string.Empty;

    /// <summary>Why the PC refused or could not apply the last exam request, or why its exam is weaker than asked; empty otherwise.</summary>
    [ObservableProperty]
    public partial string ExamProblem { get; set; } = string.Empty;

    private ExamState? _examState;
    private DateTimeOffset _examStateAt;

    /// <summary>"locked by this console", "locked by Teacher laptop", with the weakness when there is one; empty while unlocked.</summary>
    public string LockText =>
        !IsLocked ? string.Empty
        : LockReason.Length > 0 ? Strings.Format("Tile.LockedByWeak", LockedBy, LockReason)
        : Strings.Format("Tile.LockedBy", LockedBy);

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
    [NotifyPropertyChangedFor(nameof(StatusText))]
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

        // A magic packet has just gone out to this PC: what the teacher wants to know is that
        // it is being woken, not where it was the last time anybody saw it.
        TileStatus.HeldElsewhere when IsWaking => Strings.Get("Tile.Waking"),
        TileStatus.HeldElsewhere => Strings.Format("Tile.HeldBy", HeldBy),
        TileStatus.NotSeen when IsWaking => Strings.Get("Tile.Waking"),
        TileStatus.NotSeen => LastSeen.Length == 0 || LastSeen == Strings.Get("Tile.Never")
            ? Strings.Get("Tile.NotSeenYet")
            : Strings.Format("Tile.NotSeenSince", LastSeen),
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
        RefreshExamClock(now);
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

    /// <summary>
    /// The exam badge follows the PC's <c>ExamState</c> alone; an unlinked PC shows none. The
    /// words that do not move with the clock are set here, the countdown in <see cref="RefreshExamClock"/>.
    /// </summary>
    private void RefreshExam(AgentConnection? connection, DateTimeOffset now, string? thisInstanceId, Func<string, string>? nameOfInstance)
    {
        var state = connection?.ExamState;
        InExam = state is { Active: true };
        var reasons = state is null ? string.Empty : ExamReasonText(state.Reason);
        ExamProblem = string.Join("; ", new[] { connection?.ExamProblem ?? string.Empty, InExam ? reasons : string.Empty }.Where(t => t.Length > 0));
        if (!InExam || state is null)
        {
            _examState = null;
            ExamBadge = string.Empty;
            ExamText = string.Empty;
            return;
        }

        _examState = state;
        _examStateAt = connection!.ExamStateAt;
        var by = thisInstanceId is not null && string.Equals(state.SetByInstance, thisInstanceId, StringComparison.OrdinalIgnoreCase)
            ? Strings.Get("Lock.ByThisConsole")
            : state.SetByInstance.Length == 0 ? Strings.Get("Lock.ByUnknown") : nameOfInstance?.Invoke(state.SetByInstance) ?? state.SetByInstance;
        var lines = new List<string> { Strings.Format("Tile.ExamBy", by) };
        lines.Add(state.RestrictPrograms
            ? Strings.Format(state.WhitelistEnforced ? "Tile.ExamPrograms" : "Tile.ExamProgramsNotWatched", string.Join(", ", state.AllowedPrograms))
            : Strings.Get("Tile.ExamAnyProgram"));
        if (state.TerminatedRecently.Count > 0)
        {
            lines.Add(Strings.Format("Tile.ExamClosed", string.Join(", ", state.TerminatedRecently)));
        }

        if (reasons.Length > 0)
        {
            lines.Add(reasons);
        }

        ExamText = string.Join("\n", lines);
        RefreshExamClock(now);
    }

    /// <summary>The countdown on the badge: <c>remaining_seconds</c> less what has passed on this console since it arrived.</summary>
    public void RefreshExamClock(DateTimeOffset now)
    {
        if (_examState is not { } state)
        {
            return;
        }

        var name = state.PresetName.Length > 0 ? " · " + state.PresetName : string.Empty;
        if (state.UntilUnix <= 0)
        {
            ExamBadge = Strings.Get("Tile.ExamBadge") + name;
            return;
        }

        var left = Math.Max(0, state.RemainingSeconds - (long)Math.Max(0, (now - _examStateAt).TotalSeconds));
        var clock = TimeSpan.FromSeconds(left);
        var text = clock.TotalHours >= 1
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)clock.TotalHours, clock.Minutes, clock.Seconds)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}", clock.Minutes, clock.Seconds);
        ExamBadge = Strings.Format("Tile.ExamBadgeTimed", text) + name;
    }

    /// <summary>The PC's <c>ExamState.reason</c> codes in words; an unknown code shown as itself, never hidden.</summary>
    public static string ExamReasonText(string reason) =>
        string.Join("; ", OverlayReasons.Split(reason).Select(code =>
            ExamReasons.IsKnown(code)
                ? Strings.Get("Exam.Reason." + code[(code.IndexOf('.') + 1)..])
                : code));

    /// <summary>The lock badge follows the PC's <c>OverlayState</c> alone; an unlinked PC shows none (it may well still be locked, but this console cannot see it).</summary>
    private void RefreshLock(AgentConnection? connection, string? thisInstanceId, Func<string, string>? nameOfInstance)
    {
        var state = connection?.OverlayState;
        IsLocked = connection?.OverlayLocked == true;
        IsBroadcasting = connection?.OverlayBroadcasting == true;
        LockProblem = connection?.OverlayProblem ?? string.Empty;
        if (!IsLocked || state is null)
        {
            LockedBy = string.Empty;
            LockReason = string.Empty;
            return;
        }

        LockedBy = thisInstanceId is not null && string.Equals(state.SetByInstance, thisInstanceId, StringComparison.OrdinalIgnoreCase)
            ? Strings.Get("Lock.ByThisConsole")
            : state.SetByInstance.Length == 0
                ? Strings.Get("Lock.ByUnknown")
                : nameOfInstance?.Invoke(state.SetByInstance) ?? state.SetByInstance;
        LockReason = state.InputBlocked ? string.Empty : state.Reason.Length > 0 ? OverlayReasonText(state.Reason) : Strings.Get("Lock.InputNotBlocked");
    }

    /// <summary>The internet badge follows the PC's <c>InternetState</c> alone; an unlinked PC shows none.</summary>
    private void RefreshInternet(AgentConnection? connection, string? thisInstanceId, Func<string, string>? nameOfInstance)
    {
        var state = connection?.InternetState;
        InternetMode = connection?.InternetMode ?? InternetPolicy.Types.Mode.Open;
        var reasons = state is null ? string.Empty : InternetReasonText(state.Reason);
        InternetProblem = string.Join("; ", new[] { connection?.InternetProblem ?? string.Empty, InternetMode == InternetPolicy.Types.Mode.Open ? reasons : string.Empty }
            .Where(t => t.Length > 0));
        if (!HasInternetPolicy || state is null)
        {
            InternetPreset = string.Empty;
            InternetText = string.Empty;
            return;
        }

        InternetPreset = state.PresetName;
        var by = thisInstanceId is not null && string.Equals(state.SetByInstance, thisInstanceId, StringComparison.OrdinalIgnoreCase)
            ? Strings.Get("Lock.ByThisConsole")
            : state.SetByInstance.Length == 0 ? Strings.Get("Lock.ByUnknown") : nameOfInstance?.Invoke(state.SetByInstance) ?? state.SetByInstance;
        var minutes = (int)Math.Ceiling(state.RemainingSeconds / 60.0);
        var text = InternetMode == InternetPolicy.Types.Mode.Blocked
            ? Strings.Format("Tile.InternetBlockedBy", by, minutes)
            : Strings.Format("Tile.InternetWhitelistBy", state.AllowedHosts.Count, by, minutes);
        if (InternetMode == InternetPolicy.Types.Mode.Whitelist && state.RefusedRecently.Count > 0)
        {
            text += "\n" + Strings.Format("Tile.InternetRefused", state.RefusedRecently.Count);
        }

        InternetText = reasons.Length > 0 ? text + "\n" + reasons : text;
    }

    /// <summary>The PC's <c>InternetState.reason</c> codes in words; an unknown code shown as itself, never hidden.</summary>
    public static string InternetReasonText(string reason) =>
        string.Join("; ", OverlayReasons.Split(reason).Select(code =>
            InternetReasons.IsKnown(code)
                ? Strings.Get("Internet." + code[(code.IndexOf('.') + 1)..])
                : code.StartsWith("internet.", StringComparison.Ordinal) ? code[9..].Replace('_', ' ') : code.Replace('_', ' ')));

    /// <summary>A known capture reason in words; an unknown one as the code itself, so a new reason is never hidden.</summary>
    public static string CaptureReasonText(string reason) =>
        reason is "no_desktop" or "access_lost" or "no_duplication" or "gdi_failed" or "open_failed" or "failed"
            or "permission" or "no_display" or "unsupported" or "capture_failed"
            ? Strings.Get("Capture." + reason)
            : reason.Replace('_', ' ');

    /// <summary>
    /// The PC's <c>OverlayState.reason</c> — stable <see cref="OverlayReasons"/> codes,
    /// space-separated (D-70 item 7) — in words from the resources, joined with "; "; an
    /// unknown code is shown as itself (without its <c>overlay.</c> prefix), never hidden.
    /// </summary>
    public static string OverlayReasonText(string reason) =>
        string.Join("; ", OverlayReasons.Split(reason).Select(code =>
            OverlayReasons.IsKnown(code)
                ? Strings.Get("Overlay." + code[(code.IndexOf('.') + 1)..])
                : code.StartsWith("overlay.", StringComparison.Ordinal) ? code[8..].Replace('_', ' ') : code.Replace('_', ' ')));

    /// <param name="ownership">
    /// What the session actually knows about this PC (M5 §4.6, D-58). <c>null</c> means
    /// "nothing is known and no other console is live", which is what an offline PC looks
    /// like; a PC is shown as held by another machine only for a positive observation.
    /// </param>
    /// <param name="thisInstanceId">This console's instance id, so a lock it set reads "by this console".</param>
    /// <param name="nameOfInstance">Names another console from its instance id (<see cref="LabSession.NameOfInstance"/>); the id itself when <c>null</c>.</param>
    public void Refresh(MachineRecord machine, AgentConnection? connection, MachineOwnership? ownership, DateTimeOffset now, bool waking = false,
        string? thisInstanceId = null, Func<string, string>? nameOfInstance = null)
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
        RefreshLock(connection, thisInstanceId, nameOfInstance);
        RefreshInternet(connection, thisInstanceId, nameOfInstance);
        RefreshExam(connection, now, thisInstanceId, nameOfInstance);
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
            LastSeen = machine.LastSeenUnix == 0
                ? Strings.Get("Tile.Never")
                : DateTimeOffset.FromUnixTimeSeconds(machine.LastSeenUnix).ToLocalTime().ToString("g", Strings.Culture);
            HeldBy = ownership?.HolderName ?? string.Empty;
            Status = ownership?.Kind switch
            {
                OwnershipKind.ObservedElsewhere => TileStatus.HeldElsewhere,
                OwnershipKind.Unknown => TileStatus.NotSeen,
                _ => TileStatus.Offline,
            };
        }

        RefreshPicture(now, connection is not null);
    }
}

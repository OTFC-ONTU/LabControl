namespace LabControl.Shared.Identity;

/// <summary>What a certificate issued by the lab key is allowed to be.</summary>
public enum LabRole
{
    Unknown = 0,

    /// <summary>The lab key itself — the private certificate authority (ARCHITECTURE §3.1).</summary>
    Authority = 1,

    /// <summary>One console installation on one teacher machine.</summary>
    Console = 2,

    /// <summary>One student PC.</summary>
    Agent = 3,
}

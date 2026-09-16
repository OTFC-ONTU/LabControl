using LabControl.Shared.Identity;

namespace LabControl.Shared.Policy;

/// <summary>
/// Who may set or lift a policy on this PC (M6, D-69): the console the link is bound to,
/// judged <b>before</b> the message is applied, on the same read loop that merges
/// <c>RevocationState</c>. It fails closed — no validated instance means no authority — where
/// <c>AgentLink.LeaveIfConsoleRevoked</c> does nothing with a <c>null</c> instance, because
/// leaving a link that is already gone is harmless and applying a lock for nobody is not.
/// Any console of the same lab may end a policy it can see (owner decision 4), so the gate
/// never compares the sender with the session's <c>set_by_instance</c>.
/// </summary>
public static class OwnershipGate
{
    /// <summary>
    /// The reason a message on this link may not touch a policy, or <c>null</c> when it may.
    /// </summary>
    /// <param name="linkedInstanceId">The validated instance the link is up with; <c>null</c> while unlinked.</param>
    /// <param name="boundInstanceId">The instance every job on the link is bound to; <c>null</c> once the link is coming down.</param>
    /// <param name="consoleSerial">The serial of the console leaf the handshake validated.</param>
    /// <param name="revocations">Everything this PC holds revoked, as merged so far.</param>
    public static string? Refusal(string? linkedInstanceId, string? boundInstanceId, string? consoleSerial, RevocationSet revocations)
    {
        ArgumentNullException.ThrowIfNull(revocations);

        if (linkedInstanceId is null || boundInstanceId is null)
        {
            return PolicyEvents.RefusedUnbound;
        }

        if (!string.Equals(linkedInstanceId, boundInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            return PolicyEvents.RefusedUnbound;
        }

        if (consoleSerial is not null && revocations.IsRevoked(consoleSerial))
        {
            return PolicyEvents.RefusedRevoked;
        }

        if (revocations.IsRevoked(LabCertificates.InstanceSerial(linkedInstanceId)))
        {
            return PolicyEvents.RefusedWithdrawn;
        }

        return null;
    }
}

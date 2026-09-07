using LabControl.Shared;
using LabControl.Shared.Setup;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace LabControl.Setup;

/// <summary>Power API adapter; no shell, localized output parsing or battery writes.</summary>
internal sealed class WindowsPowerPlanSystem : IPowerPlanSystem
{
    public unsafe Guid GetActiveScheme()
    {
        Guid* scheme = null;
        try
        {
            Check((uint)PInvoke.PowerGetActiveScheme(default, &scheme));
            if (scheme is null) throw new IOException("Windows did not return an active power scheme.");
            return *scheme;
        }
        finally
        {
            if (scheme is not null) PInvoke.LocalFree(new HLOCAL(scheme));
        }
    }

    public unsafe uint ReadAcSeconds(Guid scheme, PowerPlanPolicy policy)
    {
        var (subgroup, setting) = Location(policy);
        uint seconds;
        Check((uint)PInvoke.PowerReadACValueIndex(default, &scheme, &subgroup, &setting, &seconds));
        return seconds;
    }

    public unsafe void WriteAcSeconds(Guid scheme, PowerPlanPolicy policy, uint seconds)
    {
        var (subgroup, setting) = Location(policy);
        Check((uint)PInvoke.PowerWriteACValueIndex(default, &scheme, &subgroup, &setting, seconds));
    }

    public unsafe void ActivateScheme(Guid scheme) => Check((uint)PInvoke.PowerSetActiveScheme(default, &scheme));

    private static (Guid Subgroup, Guid Setting) Location(PowerPlanPolicy policy) => policy switch
    {
        PowerPlanPolicy.Sleep => (Defaults.PowerSleepSubgroup, Defaults.PowerSleepTimeout),
        PowerPlanPolicy.Display => (Defaults.PowerDisplaySubgroup, Defaults.PowerDisplayTimeout),
        PowerPlanPolicy.Disk => (Defaults.PowerDiskSubgroup, Defaults.PowerDiskTimeout),
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    private static void Check(uint status)
    {
        if (status != 0) throw new IOException($"The Windows power operation failed (status {status}); preserve settings for review.");
    }
}

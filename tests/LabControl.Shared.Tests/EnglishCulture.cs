using System.Globalization;
using System.Runtime.CompilerServices;

namespace LabControl.Shared.Tests;

/// <summary>
/// The tests assert English text; the product picks its language from the OS (M7, D-86), and
/// the owner's Mac reports ru-UA. Every thread of this test run renders in English unless a
/// test chooses another language on purpose.
/// </summary>
internal static class EnglishCulture
{
    [ModuleInitializer]
    internal static void Pin()
    {
        var english = CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}

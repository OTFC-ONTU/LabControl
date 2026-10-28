namespace LabControl.Shared.Localization;

/// <summary>
/// The language a PC writes to its teacher in (M7, D-87): event messages, job results and
/// refusals. It is the language of the last console that linked — <c>Welcome.language</c> —
/// kept in <c>agent.json</c> so what the PC reports after a restart, before any console is
/// back, is still in that language. English until a console has said otherwise.
/// <para>
/// One value for the process, because a real PC has one link; the simulator's PCs share a
/// process and are all linked to the same console. The stock words on the student's screen do
/// not use it: they follow the session that put them there (<c>PolicySession.Language</c>).
/// </para>
/// </summary>
public static class PcLanguage
{
    private static volatile ProductLanguage _current = ProductLanguages.English;

    public static ProductLanguage Current => _current;

    /// <summary>Takes the language a code names; empty or unknown is English (<see cref="ProductLanguages.Of"/>).</summary>
    public static void Set(string? code) => _current = ProductLanguages.Of(code);
}

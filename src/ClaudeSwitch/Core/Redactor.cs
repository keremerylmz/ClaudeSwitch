namespace ClaudeSwitch.Core;

/// <summary>
/// Masks emails and org names for screenshots and screen-sharing.
///
/// The owner redacts README screenshots by hand today; one toggle does it everywhere instead.
/// Purely a display transform — nothing stored is altered, so turning it off brings the real
/// text straight back.
/// </summary>
internal static class Redactor
{
    public static bool Enabled => App.Settings.Redact;

    /// <summary>
    /// Masks <paramref name="text"/> when redaction is on. Emails keep their shape (<c>••••@••••</c>)
    /// so a row still reads as an account; anything else collapses to a fixed run of dots so the
    /// length can't hint at the original.
    /// </summary>
    public static string Mask(string? text)
    {
        if (!Enabled || string.IsNullOrEmpty(text)) return text ?? "";

        var at = text.IndexOf('@');
        if (at > 0 && at < text.Length - 1)
            return "••••@••••";

        return "••••••";
    }
}

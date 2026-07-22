using Color = System.Windows.Media.Color;

namespace ClaudeSwitch.Core;

/// <summary>
/// A stable, offline colour for an account that has no colour of its own.
///
/// Derived from the email so the same account always looks the same, across restarts and across
/// machines — which means a multi-account list is scannable the moment you add the accounts,
/// without anyone assigning colours by hand. No network: this is not Gravatar, just a hash.
/// </summary>
internal static class Identicon
{
    /// <summary>
    /// A medium, legible colour keyed to <paramref name="seed"/>. Saturation and lightness are
    /// fixed so every generated avatar reads the same weight and carries white text on either
    /// theme; only the hue varies.
    /// </summary>
    public static Color ColorFor(string seed)
    {
        var hue = StableHash(seed.Trim().ToLowerInvariant()) % 360u;
        return FromHsl(hue, 0.52, 0.52);
    }

    /// <summary>
    /// FNV-1a. .NET's string.GetHashCode is randomized per process, so it would give the same
    /// account a different colour on every launch — the one thing this must not do.
    /// </summary>
    private static uint StableHash(string s)
    {
        var hash = 2166136261u;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = l - c / 2;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}

using System.Text;

namespace ClaudeSwitch.Core;

/// <summary>
/// Text-level editor for top-level members of a JSON object.
///
/// Why not System.Text.Json? Claude Code's ~/.claude.json is written by a JS client that
/// tolerates duplicate keys — real files in the wild contain the same project path twice
/// under different casing. JsonNode.Parse throws on those, and a parse/serialize round-trip
/// would also reorder members and drop the original formatting of a 45KB file holding the
/// user's entire project history.
///
/// So we never reparse the document. We locate the exact character span of the member we
/// care about and splice around it; every other byte survives untouched.
/// </summary>
internal static class JsonSurgeon
{
    /// <summary>Span of one <c>"key": value</c> member inside the root object.</summary>
    internal readonly record struct Member(int KeyStart, int ValueStart, int ValueEnd)
    {
        /// <summary>Index just past the member, i.e. where a following ',' would sit.</summary>
        public int MemberEnd => ValueEnd;
    }

    /// <summary>
    /// Finds a member of the ROOT object by key. Nested keys are never matched.
    /// On duplicate keys the first occurrence wins — matching the "last write in, first read out"
    /// behaviour we need, since we only ever rewrite the span we found.
    /// </summary>
    public static bool TryFindMember(string json, string key, out Member member)
    {
        member = default;
        var i = SkipWhitespace(json, 0);
        if (i >= json.Length || json[i] != '{') return false;
        i++; // past '{'

        while (true)
        {
            i = SkipWhitespace(json, i);
            if (i >= json.Length) return false;
            if (json[i] == '}') return false;      // end of root object, no match
            if (json[i] == ',') { i++; continue; }
            if (json[i] != '"') return false;      // malformed — bail rather than corrupt

            var keyStart = i;
            if (!TryReadString(json, ref i, out var foundKey)) return false;

            i = SkipWhitespace(json, i);
            if (i >= json.Length || json[i] != ':') return false;
            i++; // past ':'
            i = SkipWhitespace(json, i);

            var valueStart = i;
            if (!TrySkipValue(json, ref i)) return false;
            var valueEnd = i;

            if (string.Equals(foundKey, key, StringComparison.Ordinal))
            {
                member = new Member(keyStart, valueStart, valueEnd);
                return true;
            }
        }
    }

    /// <summary>Returns the raw JSON text of a root member's value, or null when absent.</summary>
    public static string? GetRawValue(string json, string key)
        => TryFindMember(json, key, out var m)
            ? json[m.ValueStart..m.ValueEnd]
            : null;

    /// <summary>
    /// Sets a root member to <paramref name="rawValue"/> (raw JSON text, not a string literal —
    /// pass <c>"\"abc\""</c> for the string "abc"). Replaces in place when the key exists,
    /// otherwise inserts as the first member so we never disturb the trailing structure.
    /// </summary>
    public static string SetRawValue(string json, string key, string rawValue)
    {
        if (TryFindMember(json, key, out var m))
        {
            return string.Concat(json.AsSpan(0, m.ValueStart), rawValue, json.AsSpan(m.ValueEnd));
        }

        var open = SkipWhitespace(json, 0);
        if (open >= json.Length || json[open] != '{')
            throw new InvalidDataException("Root of the document is not a JSON object.");

        var after = SkipWhitespace(json, open + 1);
        var isEmptyObject = after < json.Length && json[after] == '}';

        var sb = new StringBuilder(json.Length + rawValue.Length + key.Length + 8);
        sb.Append(json, 0, open + 1);
        sb.Append('"').Append(Escape(key)).Append("\":").Append(rawValue);
        if (!isEmptyObject) sb.Append(',');
        sb.Append(json, open + 1, json.Length - open - 1);
        return sb.ToString();
    }

    /// <summary>
    /// Removes a root member entirely, along with exactly one adjacent comma so the
    /// document stays well-formed whether the member was first, middle, or last.
    /// No-op when the key is absent.
    /// </summary>
    public static string RemoveMember(string json, string key)
    {
        if (!TryFindMember(json, key, out var m)) return json;

        var cut_from = m.KeyStart;
        var cut_to = m.ValueEnd;

        // Prefer eating the trailing comma; fall back to the leading one for a last member.
        var probe = SkipWhitespace(json, cut_to);
        if (probe < json.Length && json[probe] == ',')
        {
            cut_to = probe + 1;
        }
        else
        {
            var back = cut_from - 1;
            while (back >= 0 && char.IsWhiteSpace(json[back])) back--;
            if (back >= 0 && json[back] == ',') cut_from = back;
        }

        return string.Concat(json.AsSpan(0, cut_from), json.AsSpan(cut_to));
    }

    /// <summary>Convenience: reads a root member that holds a JSON string.</summary>
    public static string? GetStringValue(string json, string key)
    {
        var raw = GetRawValue(json, key);
        if (raw is null || raw.Length < 2 || raw[0] != '"') return null;
        var i = 0;
        return TryReadString(raw, ref i, out var s) ? s : null;
    }

    /// <summary>Encodes a .NET string as a JSON string literal, quotes included.</summary>
    public static string ToJsonString(string value) => $"\"{Escape(value)}\"";

    // ---- scanning primitives -------------------------------------------------

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>Reads a JSON string starting at the opening quote; leaves <paramref name="i"/> past the closing quote.</summary>
    private static bool TryReadString(string s, ref int i, out string value)
    {
        value = string.Empty;
        if (i >= s.Length || s[i] != '"') return false;
        i++;

        var sb = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"') { i++; value = sb.ToString(); return true; }
            if (c == '\\')
            {
                i++;
                if (i >= s.Length) return false;
                var e = s[i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'u':
                        if (i + 4 >= s.Length) return false;
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                        i += 4;
                        break;
                    default: return false;
                }
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return false; // unterminated
    }

    /// <summary>Advances <paramref name="i"/> past one complete JSON value of any type.</summary>
    private static bool TrySkipValue(string s, ref int i)
    {
        if (i >= s.Length) return false;

        switch (s[i])
        {
            case '"':
                return TryReadString(s, ref i, out _);

            case '{':
            case '[':
                return TrySkipContainer(s, ref i);

            default:
                // number / true / false / null — runs until a structural character
                var start = i;
                while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
                return i > start;
        }
    }

    /// <summary>Brace/bracket matching that ignores structural characters inside strings.</summary>
    private static bool TrySkipContainer(string s, ref int i)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                if (!TryReadString(s, ref i, out _)) return false;
                continue;
            }
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']')
            {
                depth--;
                if (depth == 0) { i++; return true; }
            }
            i++;
        }
        return false; // unbalanced
    }

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.Core;

/// <summary>
/// The pure text transforms behind the Claude Code integrations — no file I/O, so the one part
/// that can damage a user's ~/.claude/settings.json is directly testable.
///
/// On a real install that file is dominated by a large "permissions" block. None of it is ever
/// parsed: <see cref="JsonSurgeon"/> splices the single root member being changed, and only the
/// small "hooks" value is read as JSON.
/// </summary>
internal static class SettingsEditor
{
    /// <summary>Marker that identifies an entry as ours, in both the command and any check.</summary>
    public const string StatusLineMarker = "statusline.ps1";
    public const string LimitHookMarker = "limit-hook.ps1";

    public static string SetStatusLine(string settingsJson, string command)
    {
        var value = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["padding"] = 0,
        };

        return JsonSurgeon.SetRawValue(settingsJson, "statusLine", value.ToJsonString());
    }

    public static string RemoveStatusLine(string settingsJson)
        => JsonSurgeon.RemoveMember(settingsJson, "statusLine");

    /// <summary>
    /// Adds our StopFailure/rate_limit handler, replacing an older copy of itself but leaving
    /// every other hook — including other StopFailure matchers — exactly as it found them.
    /// </summary>
    public static string SetLimitHook(string settingsJson, string command)
    {
        var hooks = ReadHooks(settingsJson);

        var events = hooks["StopFailure"] as JsonArray;
        if (events is null) { events = []; hooks["StopFailure"] = events; }

        StripOurGroups(events);
        events.Add(new JsonObject
        {
            ["matcher"] = "rate_limit",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                // Fire-and-forget: this runs on an already-failed turn, and Claude Code ignores
                // StopFailure output anyway, so there is nothing to gain by blocking.
                ["async"] = true,
                ["timeout"] = 15,
            }),
        });

        return JsonSurgeon.SetRawValue(settingsJson, "hooks", hooks.ToJsonString());
    }

    public static string RemoveLimitHook(string settingsJson)
    {
        var hooks = ReadHooks(settingsJson);

        if (hooks["StopFailure"] is JsonArray events)
        {
            StripOurGroups(events);
            // Leave no empty scaffolding behind — an uninstall should be invisible.
            if (events.Count == 0) hooks.Remove("StopFailure");
        }

        return hooks.Count == 0
            ? JsonSurgeon.RemoveMember(settingsJson, "hooks")
            : JsonSurgeon.SetRawValue(settingsJson, "hooks", hooks.ToJsonString());
    }

    /// <summary>True when the named root member exists and points at our script.</summary>
    public static bool IsOurs(string settingsJson, string key, string marker)
        => JsonSurgeon.GetRawValue(settingsJson, key)?
            .Contains(marker, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The hooks value as a mutable object. Only this member is parsed — never the whole file.
    /// </summary>
    private static JsonObject ReadHooks(string settingsJson)
    {
        var raw = JsonSurgeon.GetRawValue(settingsJson, "hooks");
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "The \"hooks\" section of ~/.claude/settings.json couldn't be read, so it was left alone.");
        }
    }

    private static void StripOurGroups(JsonArray events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i]?.ToJsonString().Contains(LimitHookMarker, StringComparison.OrdinalIgnoreCase) == true)
                events.RemoveAt(i);
        }
    }
}

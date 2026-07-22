using ClaudeSwitch.Core;

// A dependency-free harness for the one piece of this app that can destroy user data:
// the in-place editor for ~/.claude.json. Run it with a path to a real config to prove the
// edits are surgical. Falls back to synthetic cases when no path is given.
//
//   dotnet run --project tests/SurgeonTests -- "C:\path\to\a\copy\of\.claude.json"

var failures = 0;
var checks = 0;

void Check(string name, bool ok, string? detail = null)
{
    checks++;
    if (ok) { Console.WriteLine($"  PASS  {name}"); return; }
    failures++;
    Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : $"  -> {detail}")}");
}

Console.WriteLine("=== Synthetic cases ===");

// Round-trip on a minimal document.
const string simple = """{"a":1,"oauthAccount":{"emailAddress":"x@y.z"},"b":"two"}""";
Check("reads a nested object value",
    JsonSurgeon.GetRawValue(simple, "oauthAccount") == """{"emailAddress":"x@y.z"}""");
Check("reads a string value",
    JsonSurgeon.GetStringValue(simple, "b") == "two");
Check("returns null for a missing key",
    JsonSurgeon.GetRawValue(simple, "nope") is null);

var replaced = JsonSurgeon.SetRawValue(simple, "oauthAccount", """{"emailAddress":"new@y.z"}""");
Check("replaces in place, leaves neighbours alone",
    replaced == """{"a":1,"oauthAccount":{"emailAddress":"new@y.z"},"b":"two"}""",
    replaced);

var inserted = JsonSurgeon.SetRawValue(simple, "userID", "\"abc\"");
Check("inserts a missing key",
    inserted.Contains("\"userID\":\"abc\"") && inserted.Contains("\"b\":\"two\""), inserted);

// Removal in each position.
Check("removes a middle member",
    JsonSurgeon.RemoveMember(simple, "oauthAccount") == """{"a":1,"b":"two"}""",
    JsonSurgeon.RemoveMember(simple, "oauthAccount"));
Check("removes the first member",
    JsonSurgeon.RemoveMember(simple, "a") == """{"oauthAccount":{"emailAddress":"x@y.z"},"b":"two"}""",
    JsonSurgeon.RemoveMember(simple, "a"));
Check("removes the last member",
    JsonSurgeon.RemoveMember(simple, "b") == """{"a":1,"oauthAccount":{"emailAddress":"x@y.z"}}""",
    JsonSurgeon.RemoveMember(simple, "b"));
Check("removing an absent key is a no-op",
    JsonSurgeon.RemoveMember(simple, "zzz") == simple);

// The whole reason this class exists.
const string dupes = """{"projects":{"c:/x":1,"C:/x":2},"userID":"u1","oauthAccount":{"e":1}}""";
Check("tolerates duplicate keys in a nested object",
    JsonSurgeon.GetStringValue(dupes, "userID") == "u1");
Check("edits a document containing duplicate keys",
    JsonSurgeon.SetRawValue(dupes, "userID", "\"u2\"")
        == """{"projects":{"c:/x":1,"C:/x":2},"userID":"u2","oauthAccount":{"e":1}}""");

// Structural characters hiding inside strings must not be mistaken for delimiters.
const string tricky = """{"a":"}{,\"quoted\"","oauthAccount":{"note":"a\\b"},"z":[1,{"k":"}"}]}""";
Check("ignores braces inside string literals",
    JsonSurgeon.GetRawValue(tricky, "oauthAccount") == """{"note":"a\\b"}""",
    JsonSurgeon.GetRawValue(tricky, "oauthAccount"));
Check("skips arrays containing objects",
    JsonSurgeon.GetRawValue(tricky, "z") == """[1,{"k":"}"}]""",
    JsonSurgeon.GetRawValue(tricky, "z"));

// Only root-level keys may match, never a nested one of the same name.
const string nested = """{"outer":{"userID":"inner"},"userID":"root"}""";
Check("does not match a nested key of the same name",
    JsonSurgeon.GetStringValue(nested, "userID") == "root");

// Whitespace / pretty-printed input.
const string pretty = "{\n  \"a\" : 1 ,\n  \"userID\" : \"u\" \n}";
Check("handles pretty-printed whitespace",
    JsonSurgeon.GetStringValue(pretty, "userID") == "u");

// ---- settings.json integration edits -------------------------------------
// The status line and rate-limit hook write into ~/.claude/settings.json, whose bulk on a real
// install is the user's permission rules. These prove the edits are add-only and reversible.

Console.WriteLine("\n=== Settings integration ===");

const string slCmd = "powershell -File \"C:\\x\\statusline.ps1\"";
const string hkCmd = "powershell -File \"C:\\x\\limit-hook.ps1\"";

// A settings file shaped like a real one: a big permissions block plus other keys around ours.
const string settings =
    """{"permissions":{"allow":["Bash(git:*)","Read(/**)"],"deny":[]},"model":"opus","hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"other.sh"}]}]}}""";

var withSl = SettingsEditor.SetStatusLine(settings, slCmd);
Check("status line is added and recognised as ours",
    SettingsEditor.IsOurs(withSl, "statusLine", SettingsEditor.StatusLineMarker));
Check("adding the status line leaves permissions byte-identical",
    JsonSurgeon.GetRawValue(withSl, "permissions") == JsonSurgeon.GetRawValue(settings, "permissions"));
Check("removing the status line restores the file exactly",
    SettingsEditor.RemoveStatusLine(withSl) == settings,
    SettingsEditor.RemoveStatusLine(withSl));

var withHook = SettingsEditor.SetLimitHook(settings, hkCmd);
Check("limit hook is added and recognised as ours",
    SettingsEditor.IsOurs(withHook, "hooks", SettingsEditor.LimitHookMarker));
Check("adding the hook preserves the user's existing PreToolUse hook",
    JsonSurgeon.GetRawValue(withHook, "hooks")!.Contains("other.sh"));
Check("adding the hook leaves permissions byte-identical",
    JsonSurgeon.GetRawValue(withHook, "permissions") == JsonSurgeon.GetRawValue(settings, "permissions"));

// Removing our hook must put the user's own hooks back exactly as they were.
var hookRemoved = SettingsEditor.RemoveLimitHook(withHook);
Check("removing the hook restores hooks to the original",
    JsonSurgeon.GetRawValue(hookRemoved, "hooks") == JsonSurgeon.GetRawValue(settings, "hooks"),
    JsonSurgeon.GetRawValue(hookRemoved, "hooks"));

// Installing twice must not stack two copies of our matcher.
var twice = SettingsEditor.SetLimitHook(withHook, hkCmd);
var ourCount = System.Text.RegularExpressions.Regex.Matches(
    JsonSurgeon.GetRawValue(twice, "hooks")!, SettingsEditor.LimitHookMarker).Count;
Check("re-installing the hook does not duplicate it", ourCount == 1, $"count={ourCount}");

// When ours is the only StopFailure entry and there are no other hooks at all, removing it
// should take the empty "hooks" object away rather than leave scaffolding behind.
const string bare = """{"model":"opus"}""";
var bareHook = SettingsEditor.SetLimitHook(bare, hkCmd);
Check("hook removal from an otherwise-empty file leaves no hooks key",
    SettingsEditor.RemoveLimitHook(bareHook) == bare,
    SettingsEditor.RemoveLimitHook(bareHook));

// A pre-existing StopFailure matcher the user set up themselves must survive our uninstall.
const string userStop =
    """{"hooks":{"StopFailure":[{"matcher":"billing_error","hooks":[{"type":"command","command":"mine.sh"}]}]}}""";
var coexist = SettingsEditor.SetLimitHook(userStop, hkCmd);
Check("our hook coexists with the user's StopFailure matcher",
    JsonSurgeon.GetRawValue(coexist, "hooks")!.Contains("mine.sh")
    && SettingsEditor.IsOurs(coexist, "hooks", SettingsEditor.LimitHookMarker));
Check("removing ours keeps the user's StopFailure matcher",
    SettingsEditor.RemoveLimitHook(coexist) == userStop,
    SettingsEditor.RemoveLimitHook(coexist));

// ---- update version comparison -------------------------------------------
// This regressed once already: splitting on '.' and '-' together and taking [0] left only the
// major number, so every minor and patch release compared equal and no user was ever offered
// an update. The patch-level cases below are the ones that were broken.

Console.WriteLine("\n=== Update version compare ===");

Check("newer patch is an update", UpdateChecker.IsNewer("v0.3.1", "0.3.0"));
Check("newer minor is an update", UpdateChecker.IsNewer("v0.4.0", "0.3.9"));
Check("newer major is an update", UpdateChecker.IsNewer("v1.0.0", "0.9.9"));
Check("same version is not an update", !UpdateChecker.IsNewer("v0.3.1", "0.3.1"));
Check("older patch is not an update", !UpdateChecker.IsNewer("v0.3.0", "0.3.1"));
Check("older minor is not an update", !UpdateChecker.IsNewer("v0.2.9", "0.3.0"));
Check("tolerates a missing 'v'", UpdateChecker.IsNewer("0.3.2", "0.3.1"));
Check("ignores a prerelease suffix", UpdateChecker.IsNewer("v0.4.0-beta.1", "0.3.9"));
Check("prerelease of the same version is not newer", !UpdateChecker.IsNewer("v0.3.1-rc1", "0.3.1"));
Check("short tags are padded", UpdateChecker.IsNewer("v1.1", "1.0.9"));
Check("garbage never claims to be newer", !UpdateChecker.IsNewer("banana", "0.3.1"));

// ---- real-file test ------------------------------------------------------

if (args.Length > 0 && File.Exists(args[0]))
{
    Console.WriteLine($"\n=== Real config: {args[0]} ===");
    var original = File.ReadAllText(args[0]);
    Console.WriteLine($"  size: {original.Length:N0} chars");

    var oauth = JsonSurgeon.GetRawValue(original, "oauthAccount");
    var userId = JsonSurgeon.GetRawValue(original, "userID");
    Check("finds oauthAccount", oauth is not null);
    Check("finds userID", userId is not null);

    if (oauth is not null && userId is not null)
    {
        // Simulate a full switch: swap identity, drop the account-scoped caches.
        var fake = """{"accountUuid":"11111111-1111-1111-1111-111111111111","emailAddress":"other@example.com"}""";
        var edited = JsonSurgeon.SetRawValue(original, "oauthAccount", fake);
        edited = JsonSurgeon.SetRawValue(edited, "userID", "\"deadbeef\"");

        foreach (var cache in new[] { "modelAccessCache", "clientDataCache", "orgModelDefaultCache" })
            edited = JsonSurgeon.RemoveMember(edited, cache);

        Check("new identity is present",
            JsonSurgeon.GetRawValue(edited, "oauthAccount") == fake);
        Check("new userID is present",
            JsonSurgeon.GetStringValue(edited, "userID") == "deadbeef");
        Check("caches are gone",
            JsonSurgeon.GetRawValue(edited, "modelAccessCache") is null
            && JsonSurgeon.GetRawValue(edited, "clientDataCache") is null);

        // The single most important property: nothing else moved.
        var projectsBefore = JsonSurgeon.GetRawValue(original, "projects");
        var projectsAfter = JsonSurgeon.GetRawValue(edited, "projects");
        Check("projects section is byte-identical",
            projectsBefore is not null && projectsBefore == projectsAfter,
            projectsBefore is null ? "projects key not found" : $"{projectsBefore.Length} vs {projectsAfter?.Length}");

        foreach (var key in new[] { "numStartups", "machineID", "mcpServers", "settings", "history" })
        {
            var before = JsonSurgeon.GetRawValue(original, key);
            if (before is null) continue;
            Check($"'{key}' survived untouched", before == JsonSurgeon.GetRawValue(edited, key));
        }

        // Restoring the original values must reproduce the original document exactly,
        // minus only the caches we intentionally dropped.
        var restored = JsonSurgeon.SetRawValue(edited, "oauthAccount", oauth);
        restored = JsonSurgeon.SetRawValue(restored, "userID", userId);
        var expected = original;
        foreach (var cache in new[] { "modelAccessCache", "clientDataCache", "orgModelDefaultCache" })
            expected = JsonSurgeon.RemoveMember(expected, cache);
        Check("round-trip restores the document exactly", restored == expected,
            $"len {restored.Length} vs {expected.Length}");

        var outPath = Path.Combine(Path.GetTempPath(), "claudeswitch-surgeon-output.json");
        File.WriteAllText(outPath, edited);
        Console.WriteLine($"  edited copy written for external validation: {outPath}");
    }
}
else
{
    Console.WriteLine("\n(no real config path passed — skipped real-file test)");
}

Console.WriteLine($"\n{checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;

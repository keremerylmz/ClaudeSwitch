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

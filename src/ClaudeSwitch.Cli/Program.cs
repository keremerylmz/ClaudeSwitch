using System.Text.Json;
using ClaudeSwitch.Core;

// cswitch — command-line control for the ClaudeSwitch tray app. Works whether or not the
// tray app is running (it edits the same credential files the GUI does).

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return Help();
        try
        {
            var rest = args.Skip(1).ToArray();
            return args[0].ToLowerInvariant() switch
            {
                "list" or "ls" => List(),
                "current" => Current(),
                "switch" or "use" => Switch(rest),
                "usage" => Usage(rest),
                "version" or "--version" or "-v" => Version(),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cswitch: {ex.Message}");
            return 1;
        }
    }

    // ── commands ──────────────────────────────────────────────────────────────

    private static int List()
    {
        var store = new ProfileStore();
        var profiles = store.LoadAll();
        if (profiles.Count == 0) { Console.WriteLine("No saved accounts."); return 0; }

        var activeUuid = AccountSwitcher.CurrentAccountUuid();
        var i = 1;
        foreach (var p in profiles)
        {
            var active = !string.IsNullOrEmpty(activeUuid) &&
                         string.Equals(p.AccountUuid, activeUuid, StringComparison.OrdinalIgnoreCase);
            var plan = string.IsNullOrEmpty(p.SubscriptionType) ? "" : p.SubscriptionType.ToUpperInvariant();
            var use = p.UsageFiveHourPercent is { } f
                ? $"5h {f:0}%  7d {p.UsageSevenDayPercent:0}%"
                : "usage n/a";
            Console.WriteLine($"{(active ? "*" : " ")} {i,2}. {p.DisplayName,-34} {plan,-6} {use}");
            i++;
        }
        return 0;
    }

    private static int Current()
    {
        var email = AccountSwitcher.CurrentEmail();
        Console.WriteLine(email ?? "not signed in");
        return email is null ? 1 : 0;
    }

    private static int Switch(string[] rest)
    {
        if (rest.Length == 0) { Console.Error.WriteLine("usage: cswitch switch <index|email|name>"); return 2; }

        var store = new ProfileStore();
        var profiles = store.LoadAll();
        var target = Resolve(profiles, rest[0]);
        if (target is null) { Console.Error.WriteLine($"No account matches \"{rest[0]}\"."); return 1; }

        if (string.Equals(target.AccountUuid, AccountSwitcher.CurrentAccountUuid(), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Already on {target.DisplayName}.");
            return 0;
        }

        var switcher = new AccountSwitcher();
        switcher.SyncActiveIntoProfile(store);   // preserve the outgoing account's rotating token
        switcher.Apply(target, store.LoadSecret(target.Id));
        target.LastUsedAt = DateTimeOffset.UtcNow;
        store.Save(target);

        Console.WriteLine($"Switched to {target.DisplayName}. Restart open Claude Code sessions if they don't pick it up.");
        return 0;
    }

    private static int Usage(string[] rest)
    {
        var json = rest.Contains("--json");

        if (!File.Exists(ClaudePaths.CredentialsFile)) { Console.Error.WriteLine("Not signed in."); return 1; }
        var token = UsageApi.ExtractAccessToken(File.ReadAllText(ClaudePaths.CredentialsFile));
        if (token is null) { Console.Error.WriteLine("No access token found."); return 1; }

        var snapshot = UsageApi.FetchAsync(token).GetAwaiter().GetResult();
        if (snapshot is null) { Console.Error.WriteLine("Couldn't fetch usage (rate limit or offline)."); return 1; }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                account = AccountSwitcher.CurrentEmail(),
                five_hour = new { percent = snapshot.FiveHourPercent, resets_at = snapshot.FiveHourResetsAt },
                seven_day = new { percent = snapshot.SevenDayPercent, resets_at = snapshot.SevenDayResetsAt },
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine(AccountSwitcher.CurrentEmail());
            Console.WriteLine($"  5-hour : {snapshot.FiveHourPercent:0}%   resets {Local(snapshot.FiveHourResetsAt)}");
            Console.WriteLine($"  7-day  : {snapshot.SevenDayPercent:0}%   resets {Local(snapshot.SevenDayResetsAt)}");
        }
        return 0;
    }

    private static int Version()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        Console.WriteLine($"cswitch {v}");
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        Help();
        return 2;
    }

    private static int Help()
    {
        Console.WriteLine("""
            cswitch — Claude Code account switcher

            Usage:
              cswitch list            List saved accounts and their usage
              cswitch current         Print the active account
              cswitch switch <who>    Switch account (index, email, or name)
              cswitch usage [--json]  Show the active account's live usage
              cswitch version         Print the version

            Examples:
              cswitch switch 2
              cswitch switch work
              cswitch usage --json | jq .five_hour.percent

            The tray app doesn't need to be running.
            """);
        return 0;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static Profile? Resolve(IReadOnlyList<Profile> profiles, string token)
    {
        if (int.TryParse(token, out var idx) && idx >= 1 && idx <= profiles.Count)
            return profiles[idx - 1];

        var exact = profiles.FirstOrDefault(p =>
            p.Email.Equals(token, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Equals(token, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var matches = profiles.Where(p =>
            p.Email.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Local(DateTimeOffset? at) =>
        at is { } t ? t.ToLocalTime().ToString("d MMM HH:mm") : "-";
}

# Contributing to ClaudeSwitch

Thanks for helping out! A few things worth knowing before you start.

## Workflow

`main` is protected — nobody pushes to it directly, including maintainers. Everything goes
through a pull request that the repository owner reviews.

```bash
git checkout -b your-branch-name
# make your changes
git commit -m "Short description of what changed and why"
git push -u origin your-branch-name
gh pr create           # or open the PR from the GitHub UI
```

Then wait for review. CI runs the tests on every push.

## Before you open a PR

**Run the tests.** They exist for a real reason:

```powershell
dotnet run --project tests\SurgeonTests
```

If you touched anything that reads or writes credential files, also run them against a **copy**
of a real config — never the live one:

```powershell
copy "$env:USERPROFILE\.claude.json" "$env:TEMP\claude-config-copy.json"
dotnet run --project tests\SurgeonTests -- "$env:TEMP\claude-config-copy.json"
```

**Build cleanly.** The project builds with zero warnings; please keep it that way.

```powershell
.\build.ps1
```

## Things to be careful with

This app edits files that people's Claude Code installation depends on. A bug here doesn't just
crash the app — it can break someone's setup or leak their tokens. So:

- **Never reparse `~/.claude.json`.** Real files contain duplicate keys that break JSON parsers,
  and reserializing reorders the user's entire project history. Use `JsonSurgeon`, which splices
  exact character spans. Add a test case if you extend it.
- **Writes must stay atomic.** Use `AtomicFile` — a crash mid-write must never leave a
  half-written config.
- **Never log or persist tokens in the clear.** Secrets go through `ProfileStore`, which seals
  them with DPAPI.
- **Fail visibly, not silently wrong.** If usage data can't be fetched, show nothing rather than a
  stale or estimated number. Don't invent values the server didn't give us.

## Style

Match the surrounding code: comments explain *why* something is done, not what the line does.
The codebase and UI are English-only, including date formatting.

## Platform support

Currently Windows-only. macOS (Keychain) and Linux (keyring) backends are very welcome — the
credential and path layers are kept separate so a new backend shouldn't require touching the UI.

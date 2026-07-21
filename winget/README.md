# winget manifests

Manifests for publishing ClaudeSwitch to the [Windows Package Manager](https://github.com/microsoft/winget-pkgs).

Once a version is released and these files reference its exe URL + SHA256, submit them by opening a
PR that adds them under `manifests/k/keremerylmz/ClaudeSwitch/<version>/` in
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). After it's accepted, users can
install with:

```powershell
winget install ClaudeSwitch
```

To validate and test locally before submitting:

```powershell
winget validate --manifest winget
winget install --manifest winget
```

**Updating for a new release:** bump `PackageVersion` in all three files, point `InstallerUrl` at
the new exe, and refresh `InstallerSha256` with `Get-FileHash dist\ClaudeSwitch.exe -Algorithm SHA256`.

## A note on code signing

These builds are **unsigned**, so Windows SmartScreen may warn on first run (**More info → Run
anyway**). Signing needs an Authenticode certificate (an OV cert is ~$100–300/yr, or a free path via
[SignPath](https://signpath.io/) for OSS projects). Once a cert is available, sign in CI before
attaching the exe to the release:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a dist\ClaudeSwitch.exe
```

A signed exe removes the SmartScreen prompt and lets the winget manifest drop any override.

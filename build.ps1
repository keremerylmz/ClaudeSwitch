<#
.SYNOPSIS
    Builds ClaudeSwitch.

.DESCRIPTION
    Two output shapes, for different audiences:

      -SelfContained   ~150 MB single .exe that runs on any Windows 10/11 machine with no
                       prerequisites. This is what release downloads should be.

      (default)        ~2 MB single .exe that needs the .NET 8 Desktop Runtime installed.
                       Fine for your own machine and much faster to iterate on.

    WPF cannot be trimmed, so the self-contained size is what it is.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SelfContained
    .\build.ps1 -Test
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$Test,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\ClaudeSwitch\ClaudeSwitch.csproj'
$outDir = Join-Path $root 'publish'

if ($Test) {
    Write-Host '=== JsonSurgeon tests ===' -ForegroundColor Cyan
    dotnet run --project (Join-Path $root 'tests\SurgeonTests') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    Write-Host ''
}

$args = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '-o', $outDir,
    '--nologo'
)

if ($SelfContained) {
    $args += '--self-contained', 'true'
    Write-Host 'Self-contained build (no .NET install required, ~150 MB)...' -ForegroundColor Cyan
}
else {
    $args += '--self-contained', 'false'
    Write-Host 'Framework-dependent build (needs .NET 8 Desktop Runtime, ~2 MB)...' -ForegroundColor Cyan
}

dotnet @args
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $outDir 'ClaudeSwitch.exe'
if (Test-Path $exe) {
    $size = (Get-Item $exe).Length / 1MB
    Write-Host ''
    Write-Host ('Done: {0}  ({1:N1} MB)' -f $exe, $size) -ForegroundColor Green
}

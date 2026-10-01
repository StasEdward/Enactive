# Publishes the Windows desktop: ONE folder holding both executables.
#
# Why one folder, and why that is the point rather than a convenience:
#
#   enactive-ui.exe   the app a person opens
#   enactive.exe      what the scheduler's tick runs (`enactive.exe --due-all`)
#
# The tick used to be registered by a hand-written script on one machine, outside the repository,
# with an absolute path into `bin\Debug` typed into it. The defect log carried that as O1 and O2.
# A cleaned `bin`, or a move to Release, and the tick silently stopped finding its executable - the
# heartbeat would show the silence and could not explain it.
#
# With both executables side by side the app can resolve the tick as
# `Path.Combine(AppContext.BaseDirectory, "enactive.exe")` - no searching, no configured path to go
# stale, and "is the registered task mine, another installation's, or gone?" becomes a question
# with an answer.
#
# It also buys a guard that cannot be deleted by accident. In a SOURCE tree the two executables
# live in different `bin` folders, so `enactive.exe` is not beside `enactive-ui.exe` and a debug
# session simply has nothing to register. Registration is therefore only possible from a published
# folder - not because a condition in the code says so, but because the file is not there. A check
# that does not exist cannot be removed by the next refactor.

[CmdletBinding()]
param(
    # Framework-dependent, like the gateway (see .github/workflows/build.yml). Pass -SelfContained
    # to carry the runtime along for a machine that has no .NET 10.
    [switch] $SelfContained,

    [string] $Configuration = "Release",

    [string] $Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repo   = Split-Path -Parent $PSScriptRoot
$out    = Join-Path $repo "artifacts\desktop-$Runtime"
$uiExe  = Join-Path $out "enactive-ui.exe"
$cliExe = Join-Path $out "enactive.exe"

# A stale file left from an earlier publish is the failure Directory.Build.props already warns
# about - two version numbers in the About list mean a DLL nobody rebuilt is still sitting here.
# Start from nothing rather than hope every file is overwritten.
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# Checked by exit code rather than by letting PowerShell turn native stderr into a terminating
# error: `dotnet` writes ordinary progress to stderr, and a script that dies on that reports a
# perfectly good build as broken.
function Publish-Project([string] $project) {
    Write-Host "publishing $project"

    $args = @(
        "publish", (Join-Path $repo $project),
        "--configuration", $Configuration,
        "--runtime", $Runtime,
        "--output", $out
    )
    $args += if ($SelfContained) { "--self-contained" } else { "--no-self-contained" }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project (exit $LASTEXITCODE)" }
}

# The console first, the UI second: they share most of their dependencies, and the UI's set is the
# larger one, so whatever is common is written last by the build that needs the most of it.
Publish-Project "src\Enactive.App.Console\Enactive.App.Console.csproj"
Publish-Project "src\Enactive.App.Ui\Enactive.App.Ui.csproj"

# The adjacency is the whole contract this folder exists to provide, so it is ASSERTED and not
# assumed. A publish that quietly produced only one of the two would leave an installation that
# looks complete, opens fine, and can never register a schedule.
foreach ($exe in @($uiExe, $cliExe)) {
    if (-not (Test-Path $exe)) {
        throw "published, but $(Split-Path -Leaf $exe) is not in $out - the two executables must sit side by side or the scheduler cannot be registered"
    }
}

Write-Host ""
Write-Host "Published to $out"
Get-Item $uiExe, $cliExe |
    Select-Object Name, @{ n = "SizeKb"; e = { [int]($_.Length / 1KB) } }, LastWriteTime |
    Format-Table -AutoSize

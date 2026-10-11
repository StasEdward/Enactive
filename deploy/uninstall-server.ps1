# Removes the Enactive.Server Windows service and its firewall rule (install-server.ps1 in reverse).
#
# The service account's data - %APPDATA%\Enactive of that account: settings, secrets, the users, the logs - and the
# workspaces stay. They are somebody's work and configuration, not the installation's; delete them by hand if that is
# what is meant. -RemoveFiles also deletes the installed program folder.

[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles "Enactive Server"),
    [switch] $RemoveFiles
)

$ErrorActionPreference = "Stop"
$ServiceName = "Enactive"
$RuleName = "Enactive Server (HTTPS)"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this from an elevated PowerShell." }

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    # A planned stop: the service cancels its runs at their next step boundary and writes their checkpoints.
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Host "service removed"
}

Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "firewall rule removed"

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "removed $InstallDir"
}

Write-Host "The service account's %APPDATA%\Enactive and the workspaces are left as they are."

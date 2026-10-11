# Installs Enactive.Server as a Windows service on this machine (ADR 0002, server plan 1.2).
#
# What it does, in order:
#   1. publishes src\Enactive.Server into -InstallDir
#   2. writes server.json beside it: where it listens, the subnets it answers, the names it is reached by,
#      the certificate - nothing defaulting open (see ServerOptions)
#   3. lets the service account read the certificate's private key, and log on as a service
#   4. registers the Event Log source "Enactive" and the service itself, under the account
#   5. opens the port in Windows Firewall for the given subnets, Domain and Private profiles only - never Public
#   6. starts it, and says where to look if it does not stay up
#
# The service runs as a USER, never LocalSystem (ADR 0002 §7): the secrets it reads are DPAPI-protected for that
# account, and the workspaces and git credentials it needs are that account's.
#
# Example:
#   .\deploy\install-server.ps1 -ServiceAccount ".\svc-enactive" -Port 8443 `
#       -Subnets 10.20.0.0/16 -HostNames enactive.lab.local -WorkspaceRoots D:\work -CertificateThumbprint 3F2A...
#
# Run from an elevated PowerShell. The account's password is asked for, never passed as an argument.

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ServiceAccount,
    [Parameter(Mandatory)] [string[]] $Subnets,
    [Parameter(Mandatory)] [string[]] $HostNames,
    # The folders under which tasks are run from the browser; none, and no task is started from it.
    [Parameter(Mandatory)] [string[]] $WorkspaceRoots,
    # Who is mailed what arrives in the inbox (through the mail server the settings name); none, no mail.
    [string[]] $NoticeRecipients = @(),
    [int] $Port = 8443,
    # The address to listen on; every address of the machine by default. The subnets and the firewall rule decide
    # who reaches it.
    [string] $ListenAddress = "0.0.0.0",
    # A certificate in LocalMachine\My (the CA's), or a .pfx file without a password. A .pfx WITH one: protect its
    # password as the service account (runas /user:<account> "Enactive.Server.exe --protect") and put it in
    # server.json yourself - DPAPI ties it to that account, and this script runs as an administrator.
    [string] $CertificateThumbprint,
    [string] $PfxPath,
    [string] $InstallDir = (Join-Path $env:ProgramFiles "Enactive Server"),
    [switch] $SelfContained
)

$ErrorActionPreference = "Stop"
$ServiceName = "Enactive"
$RuleName = "Enactive Server (HTTPS)"

# ── the settings file: the part that is tested (install-server.test.ps1) ────────────────────────────────────────

function New-ServerConfig {
    param([string[]] $Listen, [string[]] $Subnets, [string[]] $HostNames, [string] $Thumbprint, [string] $Pfx,
          [string[]] $Roots = @(), [string[]] $Notices = @())

    if (-not $Thumbprint -and -not $Pfx) { throw "Name a certificate: -CertificateThumbprint or -PfxPath." }
    if ($Thumbprint -and $Pfx) { throw "Name one certificate, not both." }
    foreach ($root in $Roots) {
        if (-not [System.IO.Path]::IsPathFullyQualified($root)) { throw "'$root' is not a full path (D:\work)." }
    }
    foreach ($subnet in $Subnets) {
        if ($subnet -notmatch '/\d+$') { throw "'$subnet' is not a subnet in CIDR (10.20.0.0/16)." }
    }

    $certificate = if ($Thumbprint) { [ordered]@{ Thumbprint = ($Thumbprint -replace '\s', ''); Store = "LocalMachine" } }
                   else { [ordered]@{ Path = $Pfx } }
    [ordered]@{
        Listen         = @($Listen)
        AllowedSubnets = @($Subnets)
        HostNames      = @($HostNames)
        WorkspaceRoots = @($Roots)
        NoticeRecipients = @($Notices)
        Certificate    = $certificate
        MaxActiveRuns  = 2
    } | ConvertTo-Json -Depth 4
}

# Dot-sourced by the test: the functions above, nothing below.
if ($MyInvocation.InvocationName -eq '.') { return }

# ── the installation ────────────────────────────────────────────────────────────────────────────────────────────

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this from an elevated PowerShell: it registers a service and a firewall rule." }

if ($ServiceAccount -match '^(NT AUTHORITY\\)?(SYSTEM|LocalSystem|LocalService|NetworkService)$') {
    throw "The service runs as a user, not ${ServiceAccount}: its secrets are protected for that user (ADR 0002 §7)."
}

$config = New-ServerConfig -Listen "$($ListenAddress):$Port" -Subnets $Subnets -HostNames $HostNames `
    -Thumbprint $CertificateThumbprint -Pfx $PfxPath -Roots $WorkspaceRoots -Notices $NoticeRecipients
$credential = Get-Credential -UserName $ServiceAccount -Message "Password of the account the Enactive service runs as"

# 1. publish
$repo = Split-Path -Parent $PSScriptRoot
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "stopping the running service to replace its files"
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
}
$publish = @("publish", (Join-Path $repo "src\Enactive.Server\Enactive.Server.csproj"), "--configuration", "Release",
             "--runtime", "win-x64", "--output", $InstallDir)
$publish += if ($SelfContained) { "--self-contained" } else { "--no-self-contained" }
& dotnet @publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
$exe = Join-Path $InstallDir "Enactive.Server.exe"

# 2. settings - readable by the service account and administrators, written by administrators only
$configPath = Join-Path $InstallDir "server.json"
Set-Content -Path $configPath -Value $config -Encoding UTF8
icacls $configPath /inheritance:r /grant:r "Administrators:F" "SYSTEM:F" "$($ServiceAccount):R" | Out-Null

# 3a. the private key, for the account
if ($CertificateThumbprint) {
    $thumb = $CertificateThumbprint -replace '\s', ''
    $cert = Get-Item "Cert:\LocalMachine\My\$thumb" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "No certificate $thumb in LocalMachine\My." }
    $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if (-not $key) { throw "The certificate $thumb has no private key on this machine." }
    $keyFile = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($key.Key.UniqueName)"
    if (-not (Test-Path $keyFile)) { $keyFile = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($key.Key.UniqueName)" }
    icacls $keyFile /grant "$($ServiceAccount):R" | Out-Null
}
elseif ($PfxPath) {
    icacls $PfxPath /inheritance:r /grant:r "Administrators:F" "SYSTEM:F" "$($ServiceAccount):R" | Out-Null
}

# 3b. "Log on as a service" - without it the service manager cannot start the service as that account
$sid = (New-Object Security.Principal.NTAccount $ServiceAccount).Translate([Security.Principal.SecurityIdentifier]).Value
$policy = Join-Path $env:TEMP "enactive-secpol.inf"
secedit /export /cfg $policy /areas USER_RIGHTS | Out-Null
$lines = Get-Content $policy
$right = $lines | Where-Object { $_ -like "SeServiceLogonRight*" }
if (-not $right -or $right -notmatch [regex]::Escape("*$sid")) {
    $updated = if ($right) { $lines -replace '^SeServiceLogonRight\s*=\s*(.*)$', "SeServiceLogonRight = `$1,*$sid" }
               else { $lines -replace '^\[Privilege Rights\]$', "[Privilege Rights]`r`nSeServiceLogonRight = *$sid" }
    Set-Content $policy $updated -Encoding Unicode
    secedit /configure /db (Join-Path $env:TEMP "enactive-secpol.sdb") /cfg $policy /areas USER_RIGHTS | Out-Null
}
Remove-Item $policy -ErrorAction SilentlyContinue

# 4. Event Log source and the service
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    New-EventLog -LogName Application -Source $ServiceName
}
$binary = "`"$exe`" --config `"$configPath`""
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    sc.exe config $ServiceName binPath= $binary obj= $ServiceAccount password= $credential.GetNetworkCredential().Password | Out-Null
} else {
    New-Service -Name $ServiceName -BinaryPathName $binary -Credential $credential -StartupType Automatic `
        -DisplayName "Enactive Server" -Description "Enactive on the local network, with a web UI." | Out-Null
}
# Restart on a crash, twice, a minute apart; a third crash in a day leaves it stopped for somebody to look at.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000// | Out-Null

# 5. firewall: the subnets, Domain and Private only
Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Protocol TCP -LocalPort $Port `
    -RemoteAddress $Subnets -Profile Domain, Private -Action Allow | Out-Null

# 6. start
Start-Service $ServiceName
Start-Sleep -Seconds 3
$state = (Get-Service $ServiceName).Status
if ($state -ne "Running") {
    Write-Warning "The service is $state. Its reason is in the service account's %APPDATA%\Enactive\logs\server\start.log, and in the Application event log (source $ServiceName)."
    exit 1
}
Write-Host "Enactive Server is running: https://$($HostNames[0]):$Port/  (answers $($Subnets -join ', '))"
Write-Host "Next: create the first administrator ON THIS MACHINE, as the service account (server plan 1.3)."

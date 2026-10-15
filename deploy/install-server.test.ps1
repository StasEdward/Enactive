# Checks the settings install-server.ps1 writes - the part of it that decides what the service answers - without
# installing anything. Run: pwsh deploy\install-server.test.ps1   (exit code 0 = all passed)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "install-server.ps1") -ServiceAccount x -Subnets 10.0.0.0/8 -HostNames h -WorkspaceRoots C:\w

$failures = 0
function Check([string] $name, [scriptblock] $test) {
    try { if (& $test) { Write-Host "ok   $name" } else { Write-Host "FAIL $name"; $script:failures++ } }
    catch { Write-Host "FAIL $name - $($_.Exception.Message)"; $script:failures++ }
}
function Throws([scriptblock] $action) { try { & $action | Out-Null; $false } catch { $true } }

$json = New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.20.0.0/16", "fd00:20::/32" -HostNames "enactive.lab.local" -Thumbprint "3F 2A 11" -Roots "D:\work"
$config = $json | ConvertFrom-Json

Check "listens where it was told" { $config.Listen -contains "0.0.0.0:8443" }
Check "answers only the subnets given" { ($config.AllowedSubnets -join ",") -eq "10.20.0.0/16,fd00:20::/32" }
Check "names the host it is reached by" { $config.HostNames -contains "enactive.lab.local" }
Check "a thumbprint is written without spaces, from LocalMachine" { $config.Certificate.Thumbprint -eq "3F2A11" -and $config.Certificate.Store -eq "LocalMachine" }
Check "runs only under the roots given" { ($config.WorkspaceRoots -join ",") -eq "D:\work" }
Check "a root that is not a full path is refused" { Throws { New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.0.0.0/8" -HostNames "h" -Thumbprint "AB" -Roots "work" } }
Check "mails no notices unless told who to" { @($config.NoticeRecipients).Count -eq 0 }
Check "no certificate is refused" { Throws { New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.0.0.0/8" -HostNames "h" } }
Check "two certificates are refused" { Throws { New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.0.0.0/8" -HostNames "h" -Thumbprint "AB" -Pfx "c.pfx" } }
Check "a subnet without a prefix length is refused" { Throws { New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.20.0.0" -HostNames "h" -Thumbprint "AB" } }
Check "a .pfx is written as a path" { (New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.0.0.0/8" -HostNames "h" -Pfx "C:\certs\s.pfx" | ConvertFrom-Json).Certificate.Path -eq "C:\certs\s.pfx" }

$keyRoot = Join-Path ([IO.Path]::GetTempPath()) ('enactive-keys-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path (Join-Path $keyRoot 'Microsoft\Crypto\Keys') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $keyRoot 'Microsoft\Crypto\RSA\MachineKeys') -Force | Out-Null
    $cng = [pscustomobject]@{ Key = [pscustomobject]@{ UniqueName = 'cng-key' } }
    $csp = [pscustomobject]@{ CspKeyContainerInfo = [pscustomobject]@{ UniqueKeyContainerName = 'csp-key' } }
    $cngFile = Join-Path $keyRoot 'Microsoft\Crypto\Keys\cng-key'
    $cspFile = Join-Path $keyRoot 'Microsoft\Crypto\RSA\MachineKeys\csp-key'
    [IO.File]::WriteAllText($cngFile, 'fixture')
    [IO.File]::WriteAllText($cspFile, 'fixture')
    Check 'CNG uses its unique key name' { (Get-PrivateKeyFile $cng $keyRoot) -eq $cngFile }
    Check 'CSP uses its key container name in MachineKeys' { (Get-PrivateKeyFile $csp $keyRoot) -eq $cspFile }
    Check 'an unsupported key never grants access to a directory' { Throws { Get-PrivateKeyFile ([pscustomobject]@{}) $keyRoot } }
    Check 'a missing key file is refused' { Throws { Get-PrivateKeyFile ([pscustomobject]@{ Key = @{ UniqueName = 'missing' } }) $keyRoot } }
} finally {
    # This test alone created this exact temporary directory; never clean a computed parent or a certificate store.
    $expected = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($keyRoot).StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test key path' }
    Remove-Item -LiteralPath $keyRoot -Recurse -Force
}

if ($failures -gt 0) { Write-Host "$failures failed"; exit 1 }
Write-Host "all passed"

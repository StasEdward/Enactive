# Checks the settings install-server.ps1 writes - the part of it that decides what the service answers - without
# installing anything. Run: pwsh deploy\install-server.test.ps1   (exit code 0 = all passed)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "install-server.ps1") -ServiceAccount x -Subnets 10.0.0.0/8 -HostNames h

$failures = 0
function Check([string] $name, [scriptblock] $test) {
    try { if (& $test) { Write-Host "ok   $name" } else { Write-Host "FAIL $name"; $script:failures++ } }
    catch { Write-Host "FAIL $name - $($_.Exception.Message)"; $script:failures++ }
}
function Throws([scriptblock] $action) { try { & $action; $false } catch { $true } }

$json = New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.20.0.0/16", "fd00:20::/32" -HostNames "enactive.lab.local" -Thumbprint "3F 2A 11"
$config = $json | ConvertFrom-Json

Check "listens where it was told" { $config.Listen -contains "0.0.0.0:8443" }
Check "answers only the subnets given" { ($config.AllowedSubnets -join ",") -eq "10.20.0.0/16,fd00:20::/32" }
Check "names the host it is reached by" { $config.HostNames -contains "enactive.lab.local" }
Check "a thumbprint is written without spaces, from LocalMachine" { $config.Certificate.Thumbprint -eq "3F2A11" -and $config.Certificate.Store -eq "LocalMachine" }
Check "no certificate is refused" { Throws { New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.0.0.0/8" -HostNames "h" } }
Check "two certificates are refused" { Throws { New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.0.0.0/8" -HostNames "h" -Thumbprint "AB" -Pfx "c.pfx" } }
Check "a subnet without a prefix length is refused" { Throws { New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.20.0.0" -HostNames "h" -Thumbprint "AB" } }
Check "a .pfx is written as a path" { (New-ServerConfig -Listen "0.0.0.0:8443" -Subnets "10.0.0.0/8" -HostNames "h" -Pfx "C:\certs\s.pfx" | ConvertFrom-Json).Certificate.Path -eq "C:\certs\s.pfx" }

if ($failures -gt 0) { Write-Host "$failures failed"; exit 1 }
Write-Host "all passed"

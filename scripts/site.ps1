<#
.SYNOPSIS
    Starts the website and API: scripts/site.ps1 [-Port 5080] [-NoBuild]
.DESCRIPTION
    Builds src/Npi.Web into .run/site (so building the solution later doesn't fail on files the running site holds), then
    serves it on http://<this computer>:<Port> on every network interface (Ctrl+C stops it). It reads the database through
    its RemoteMySql connection string (scripts/setup-secrets.ps1). Other computers on the network need a firewall rule for
    the port.
#>
param([int]$Port = 5080, [switch]$NoBuild)
. (Join-Path $PSScriptRoot "common.ps1")
$folder = Join-Path $RunRoot "site"
if (-not $NoBuild -or -not (Test-Path (Join-Path $folder "Npi.Web.dll"))) { $folder = Publish-Project "src/Npi.Web" "site" }
$env:ASPNETCORE_ENVIRONMENT = "Development"   # loads the site's user-secrets
$env:DOTNET_NOLOGO = "1"
Write-Host "Starting http://localhost:$Port (and this computer's address on the network) ..."
Push-Location $folder
try { & dotnet "Npi.Web.dll" --urls "http://0.0.0.0:$Port" }
finally { Pop-Location }

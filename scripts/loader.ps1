<#
.SYNOPSIS
    Runs the loader: scripts/loader.ps1 <command> [args]   e.g.  doctor | migrate | sample | run | load-file <zip>
.DESCRIPTION
    Builds src/Npi.Loader into .run/loader first (skip with -NoBuild), then runs it with the given command. The loader reads
    its settings from its user-secrets (scripts/setup-secrets.ps1) and logs to its log folder (see doctor).
#>
param([switch]$NoBuild, [Parameter(ValueFromRemainingArguments = $true)][string[]]$LoaderArgs = @())
. (Join-Path $PSScriptRoot "common.ps1")
$folder = Join-Path $RunRoot "loader"
if (-not $NoBuild -or -not (Test-Path (Join-Path $folder "Npi.Loader.dll"))) { $folder = Publish-Project "src/Npi.Loader" "loader" }
& dotnet (Join-Path $folder "Npi.Loader.dll") @LoaderArgs
exit $LASTEXITCODE

# Shared by the scripts in this folder (dot-sourced). Works in Windows PowerShell 5.1 and PowerShell 7 (Windows, Linux, macOS).
$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$RunRoot = Join-Path $RepoRoot ".run"   # published copies (git-ignored), so building never collides with a running site

# Reads one project's user-secrets (dotnet user-secrets list) into a hashtable.
function Get-UserSecrets([string]$Project) {
    $secrets = @{}
    $lines = & dotnet user-secrets list --project (Join-Path $RepoRoot $Project) 2>$null
    foreach ($line in $lines) {
        $i = "$line".IndexOf(" = ")
        if ($i -gt 0) { $secrets["$line".Substring(0, $i)] = "$line".Substring($i + 3) }
    }
    return $secrets
}

# Publishes a project to .run/<name> (Release) and returns the folder.
function Publish-Project([string]$Project, [string]$Name) {
    $out = Join-Path $RunRoot $Name
    Write-Host "Building $Project into $out ..."
    & dotnet publish (Join-Path $RepoRoot $Project) -c Release -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $Project failed (exit $LASTEXITCODE)" }
    return $out
}

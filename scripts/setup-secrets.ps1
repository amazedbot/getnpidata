<#
.SYNOPSIS
    Stores the settings the loader, the site and the tests need, as .NET user-secrets (never in the repository).
.DESCRIPTION
    scripts/setup-secrets.ps1 -Docker
        The MySQL of docker-compose.yml: the logins and passwords come from .env.
    scripts/setup-secrets.ps1 -CreateLogins
        A MySQL you installed yourself (Windows: deploy/setup-local-mysql.ps1): asks for the root password once, creates the
        database and the three logins (loader, site, tests) with random passwords, and stores them.
    scripts/setup-secrets.ps1
        Asks for each connection (server, database, user, password) of logins that already exist.
    Each run also asks for the optional settings (HUD token, SEC contact, openFDA key); Enter keeps what is stored, and
    -SkipOptional doesn't ask (scripts, CI).
    Check the result with: scripts/loader.ps1 doctor
#>
param([switch]$Docker, [switch]$CreateLogins, [switch]$SkipOptional, [string]$Server = "localhost", [int]$Port = 3306, [string]$Database = "npi")
. (Join-Path $PSScriptRoot "common.ps1")

function Set-Secret([string]$Project, [string]$Name, [string]$Value) {
    & dotnet user-secrets set --project (Join-Path $RepoRoot $Project) $Name $Value | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet user-secrets set $Name failed" }
}

function Read-Plain([string]$Prompt, [string]$Default = "") {
    $value = Read-Host ($(if ($Default) { "$Prompt [$Default]" } else { $Prompt }))
    if ($value) { return $value } else { return $Default }
}

function Read-Password([string]$Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function New-Password { return ([guid]::NewGuid().ToString("N") + [guid]::NewGuid().ToString("N").Substring(0, 8)) }

function Connection([string]$User, [string]$Password, [string]$Db) {
    $text = "Server=$Server;Port=$Port;User ID=$User;Password=$Password"
    if ($Db) { $text += ";Database=$Db" }
    return $text
}

if ($Docker) {
    $envFile = Join-Path $RepoRoot ".env"
    if (-not (Test-Path $envFile)) { throw "No .env: copy .env.example to .env, set the passwords, then run docker compose up -d." }
    $vars = @{}
    foreach ($line in Get-Content $envFile) {
        if ($line -match '^\s*([A-Z_]+)\s*=\s*(.*?)\s*$') { $vars[$Matches[1]] = $Matches[2] }
    }
    if ($vars["MYSQL_PORT"]) { $Port = [int]$vars["MYSQL_PORT"] }
    if ($vars["NPI_DATABASE"]) { $Database = $vars["NPI_DATABASE"] }
    $logins = @{
        Loader = @("npi_loader", $vars["NPI_LOADER_PASSWORD"])
        Web    = @("npi_web", $vars["NPI_WEB_PASSWORD"])
        Test   = @("npi_test", $vars["NPI_TEST_PASSWORD"])
    }
}
elseif ($CreateLogins) {
    $Server = Read-Plain "MySQL server" $Server
    $Port = [int](Read-Plain "Port" "$Port")
    $Database = Read-Plain "Database to create" $Database
    $root = Read-Password "MySQL root password"
    $logins = @{ Loader = @("npi_loader", (New-Password)); Web = @("npi_web", (New-Password)); Test = @("npi_test", (New-Password)) }
    $mysql = Join-Path $env:ProgramFiles "MySQL\MySQL Server 8.4\bin\mysql.exe"
    if (-not $env:ProgramFiles -or -not (Test-Path $mysql)) { $mysql = "mysql" }
    $sql = @"
CREATE DATABASE IF NOT EXISTS ``$Database`` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER IF NOT EXISTS 'npi_loader'@'localhost' IDENTIFIED BY '$($logins.Loader[1])';
ALTER USER 'npi_loader'@'localhost' IDENTIFIED BY '$($logins.Loader[1])';
GRANT ALL PRIVILEGES ON ``$Database``.* TO 'npi_loader'@'localhost';
GRANT SESSION_VARIABLES_ADMIN ON *.* TO 'npi_loader'@'localhost';
CREATE USER IF NOT EXISTS 'npi_web'@'localhost' IDENTIFIED BY '$($logins.Web[1])';
ALTER USER 'npi_web'@'localhost' IDENTIFIED BY '$($logins.Web[1])';
GRANT SELECT ON ``$Database``.* TO 'npi_web'@'localhost';
CREATE USER IF NOT EXISTS 'npi_test'@'localhost' IDENTIFIED BY '$($logins.Test[1])';
ALTER USER 'npi_test'@'localhost' IDENTIFIED BY '$($logins.Test[1])';
GRANT ALL PRIVILEGES ON ``npi\_test\_%``.* TO 'npi_test'@'localhost';
GRANT SESSION_VARIABLES_ADMIN ON *.* TO 'npi_test'@'localhost';
"@
    # The root password goes through a temporary option file, never the command line.
    $cnf = Join-Path ([IO.Path]::GetTempPath()) ("getnpidata-" + [guid]::NewGuid().ToString("N") + ".cnf")
    try {
        "[client]`nuser=root`npassword=$root`nhost=$Server`nport=$Port`nprotocol=TCP`n" | Set-Content -Encoding ascii $cnf
        $sql | & $mysql "--defaults-extra-file=$cnf" --batch
        if ($LASTEXITCODE -ne 0) { throw "Creating the logins failed (exit $LASTEXITCODE): check the root password and that MySQL is running." }
    }
    finally { Remove-Item $cnf -ErrorAction SilentlyContinue }
    Write-Host "Created database $Database and the logins npi_loader, npi_web and npi_test (random passwords, stored below)."
}
else {
    $Server = Read-Plain "MySQL server" $Server
    $Port = [int](Read-Plain "Port" "$Port")
    $Database = Read-Plain "Database" $Database
    $logins = @{}
    foreach ($role in @(@("Loader", "npi_loader", "loader (all privileges on the database + SESSION_VARIABLES_ADMIN)"),
                        @("Web", "npi_web", "site (SELECT on the database)"),
                        @("Test", "npi_test", "tests (all privileges on npi\_test\_% + SESSION_VARIABLES_ADMIN; Enter to skip)"))) {
        $user = Read-Plain "User for the $($role[2])" $role[1]
        $logins[$role[0]] = @($user, (Read-Password "Password for $user"))
    }
}

Set-Secret "src/Npi.Loader" "ConnectionStrings:LocalMySql" (Connection $logins.Loader[0] $logins.Loader[1] $Database)
Set-Secret "src/Npi.Web" "ConnectionStrings:RemoteMySql" (Connection $logins.Web[0] $logins.Web[1] $Database)
if ($logins.Test[1]) { Set-Secret "src/Npi.Loader" "NpiTestMySql" (Connection $logins.Test[0] $logins.Test[1] "") }
Write-Host "Stored the connection strings for the loader, the site and the tests."

if ($SkipOptional) { Write-Host "`nNext: scripts/loader.ps1 doctor"; exit 0 }
Write-Host "`nOptional settings (Enter keeps what is stored, or leaves it unset):"
$stored = Get-UserSecrets "src/Npi.Loader"
foreach ($setting in @(@("HudApiToken", "HUD USPS API token, free at https://www.huduser.gov/portal/dataset/uspszip-api.html (county search, area pages)"),
                       @("SecUserAgent", "SEC contact, 'getnpidata <your e-mail>' (SEC listings on company pages)"),
                       @("OpenFdaApiKey", "openFDA API key, free at https://open.fda.gov/apis/authentication/ (faster product data)"))) {
    $has = if ($stored[$setting[0]]) { " (set)" } else { "" }
    $value = Read-Host "$($setting[1])$has"
    if ($value) { Set-Secret "src/Npi.Loader" $setting[0] $value }
}

Write-Host "`nNext: scripts/loader.ps1 doctor"

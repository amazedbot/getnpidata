#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time setup of the local MySQL 8.4 server on the loader PC (CLAUDE.md §6).

.DESCRIPTION
    Assumes the binaries are installed (winget install Oracle.MySQL --version 8.4.9).
    Writes my.ini, initializes an empty data directory, registers the Windows service and starts it.
    The server listens on 127.0.0.1 only, has local_infile ON (the loader bulk-loads with
    LOAD DATA LOCAL), and binary logging OFF (single local server, no replication; saves disk).

    root@localhost is created WITHOUT a password by --initialize-insecure. Set one immediately
    afterwards (ALTER USER 'root'@'localhost' IDENTIFIED BY '...'); the server only listens on
    127.0.0.1, so the window is local-only.

    Safe to re-run: existing my.ini, data directory and service are left alone.
#>
param(
    [string]$ServiceName = "MySQL84",
    [string]$BaseDir = "C:\Program Files\MySQL\MySQL Server 8.4",
    [string]$ConfigDir = "C:\ProgramData\MySQL\MySQL Server 8.4",
    [string]$BufferPool = "8G"
)

$ErrorActionPreference = "Stop"
$mysqld = Join-Path $BaseDir "bin\mysqld.exe"
$ini = Join-Path $ConfigDir "my.ini"
$dataDir = Join-Path $ConfigDir "Data"

if (-not (Test-Path $mysqld)) { throw "mysqld.exe not found at $mysqld. Install MySQL 8.4 first." }
New-Item -ItemType Directory -Force $ConfigDir | Out-Null

if (-not (Test-Path $ini)) {
    $base = $BaseDir.Replace('\', '/')
    $data = $dataDir.Replace('\', '/')
    @"
[mysqld]
basedir="$base/"
datadir="$data/"
port=3306
bind-address=127.0.0.1
mysqlx=OFF
# The loader uses LOAD DATA LOCAL INFILE (CLAUDE.md §7 Stage 1.8).
local_infile=ON
# Single local server, no replication: no binary log (saves ~the size of every load).
disable-log-bin
character-set-server=utf8mb4
collation-server=utf8mb4_0900_ai_ci
innodb_buffer_pool_size=$BufferPool
innodb_redo_log_capacity=4G
max_allowed_packet=256M

[client]
port=3306
"@ | Set-Content -Encoding ascii $ini
    Write-Host "Wrote $ini"
} else {
    Write-Host "Keeping existing $ini"
}

# mysqld writes progress to stderr. Run it via Start-Process so Windows PowerShell 5.1 does not
# turn those lines into terminating errors under $ErrorActionPreference = "Stop".
function Invoke-Mysqld([string[]]$Arguments) {
    $p = Start-Process -FilePath $mysqld -ArgumentList $Arguments -NoNewWindow -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "mysqld $($Arguments -join ' ') failed (exit $($p.ExitCode))" }
}

if (-not (Test-Path (Join-Path $dataDir "mysql.ibd"))) {
    if ((Test-Path $dataDir) -and (Get-ChildItem -Force $dataDir | Select-Object -First 1)) {
        throw "$dataDir is not empty but has no mysql.ibd (half-initialized?). Inspect it, then empty it and re-run."
    }
    Invoke-Mysqld @("--defaults-file=`"$ini`"", "--initialize-insecure", "--console")
    Write-Host "Initialized $dataDir (root@localhost has no password yet: set one now)"
} else {
    Write-Host "Keeping existing data directory $dataDir"
}

if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    Invoke-Mysqld @("--install", $ServiceName, "--defaults-file=`"$ini`"")
    Set-Service $ServiceName -StartupType Automatic
}

Start-Service $ServiceName
Get-Service $ServiceName | Format-Table -AutoSize Name, Status, StartType

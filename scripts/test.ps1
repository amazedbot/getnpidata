<#
.SYNOPSIS
    Runs every test: scripts/test.ps1 [-NoMySql] [-UpdateSchema]
.DESCRIPTION
    The MySQL integration tests use the test login stored as the loader's user-secret NpiTestMySql (scripts/setup-secrets.ps1;
    it needs all privileges on npi\_test\_% databases and SESSION_VARIABLES_ADMIN). Without it, or with -NoMySql, they are
    skipped, as in CI on pull requests. -UpdateSchema rewrites db/schema.sql from the migrations (after adding one).
#>
param([switch]$NoMySql, [switch]$UpdateSchema)
. (Join-Path $PSScriptRoot "common.ps1")
$saved = @($env:NPI_TEST_MYSQL, $env:NPI_UPDATE_SCHEMA)
if (-not $NoMySql -and -not $env:NPI_TEST_MYSQL) {
    $test = (Get-UserSecrets "src/Npi.Loader")["NpiTestMySql"]
    if ($test) { $env:NPI_TEST_MYSQL = $test } else { Write-Warning "No NpiTestMySql user-secret: the MySQL tests are skipped (scripts/setup-secrets.ps1 sets it)." }
}
if ($NoMySql) { $env:NPI_TEST_MYSQL = "" }
if ($UpdateSchema) { $env:NPI_UPDATE_SCHEMA = "1" }
try {
    Push-Location $RepoRoot
    & dotnet test --solution getnpidata.sln
    exit $LASTEXITCODE
}
finally {
    Pop-Location
    $env:NPI_TEST_MYSQL, $env:NPI_UPDATE_SCHEMA = $saved
}

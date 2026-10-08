# getnpidata

A public website and REST API for searching every US healthcare provider in the
[CMS NPPES](https://download.cms.gov/nppes/NPI_Files.html) registry, for example
"all Chiropractors in Suffolk County, NY", with paged results and streamed CSV export.

```
CMS NPPES files → Npi.Loader (Windows PC, scheduled) → local MySQL → search projection
                → publish (diff sync) → Azure Database for MySQL → Npi.Web (site + /api/v1)
```

## Layout

| Path | What |
|---|---|
| `src/Npi.Core` | Models, search filter and SQL builder, DB access, CSV writer |
| `src/Npi.Loader` | Console app: discover → download → load → reference data → projection → publish |
| `src/Npi.Web` | ASP.NET Core Razor Pages site and `/api/v1` |
| `tests/` | xUnit v3 tests (Microsoft.Testing.Platform) |
| `db/migrations` | Numbered idempotent SQL migrations |
| `deploy/` | Azure and Task Scheduler setup |
| `legacy/getnpidata-vb` | The original VB.NET loader, kept until the C# loader reaches parity |

## Build

Requires the .NET 10 SDK.

```powershell
dotnet build getnpidata.sln
dotnet test
dotnet run --project src/Npi.Loader -- discover
dotnet run --project src/Npi.Web
```

Secrets (connection strings, the HUD API token) go in user-secrets, environment variables or a
git-ignored `appsettings.Local.json`, never in tracked files. See [CLAUDE.md](CLAUDE.md) for the full design.

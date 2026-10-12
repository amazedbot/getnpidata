# Azure setup and deployment

Production runs on **Azure App Service (Linux, .NET 10)** and **Azure Database for MySQL – Flexible
Server**, both in the **same region** (East US unless the owner decides otherwise). The local
loader PC publishes the search projection to the Azure database; the site reads only from it.

> **Provisioning costs money.** The owner creates these resources in the Azure portal (decided
> Oct 2026: East US, default `*.azurewebsites.net` address, no custom domain yet). The `az`
> commands in §6 are a scripted equivalent and are run only with the owner's OK.

## 1. Resources

| Resource | Suggested name | Settings |
|---|---|---|
| Resource group | `rg-getnpidata` | Region: East US |
| MySQL Flexible Server | `getnpidata-mysql` (globally unique) | Burstable **B1ms**, MySQL **8.4** LTS (8.0 reached end of life in April 2026; the local server is 8.4 too), 32 GB storage with auto-grow, 7-day backups, public access |
| Database | `npi` | `utf8mb4` / `utf8mb4_0900_ai_ci` |
| App Service plan | `plan-getnpidata` | Linux, **B1** |
| Web app | `getnpidata` (globally unique; becomes `<name>.azurewebsites.net`) | Runtime .NET 10, HTTPS only, Always On, health check `/health` |
| Application Insights (optional) | `appi-getnpidata` | Workspace-based, with a Log Analytics workspace `log-getnpidata` in the same group; retention 90 days. Usage measurement (docs/PRODUCT_PLAN.md decision 7), described on `/privacy` |

### Rough monthly cost (East US, pay-as-you-go, Oct 2026 list prices; verify in the Azure pricing calculator)

| Item | ≈ USD / month |
|---|---|
| App Service B1 Linux | 13 |
| MySQL B1ms compute | 12–15 |
| MySQL storage 32 GB | 4 |
| Application Insights (server-side requests only; the first 5 GB a month are free, then ≈ $2.30/GB; 90 days' retention included) | ≈ 0 at launch traffic |
| **Total** | **≈ 30** |

Backup storage up to the provisioned storage size is included. Scale up only if the Stage 3
performance targets are missed, and report the new cost to the owner first (CLAUDE.md §7 Stage 6.3).

## 2. MySQL server

### Networking / firewall

Public access with firewall rules:

- **Owner's home IP** – the loader PC publishes from here.
- **Azure services** – rule `0.0.0.0`–`0.0.0.0` ("Allow public access from any Azure service"),
  so the web app can connect. Tighter alternative: one rule per App Service *outbound IP*
  (Web app → Networking → Outbound addresses); these can change if the plan's SKU changes.

### Server parameters

| Parameter | Value | Why |
|---|---|---|
| `require_secure_transport` | `ON` (default) | TLS required; clients use `SslMode=Required` |
| `local_infile` | `ON` | Only if the first full publish uses `LOAD DATA LOCAL` (Stage 6.1) |
| `character_set_server` | `utf8mb4` (default) | |

### Users (least privilege)

Create two users besides the admin login:

```sql
CREATE USER 'npi_publish'@'%' IDENTIFIED BY '<strong password>';
GRANT ALL PRIVILEGES ON npi.* TO 'npi_publish'@'%';      -- loader: migrations + publish

CREATE USER 'npi_web'@'%' IDENTIFIED BY '<strong password>';
GRANT SELECT ON npi.* TO 'npi_web'@'%';                   -- site + API: read-only
```

Store the passwords in a password manager. Never commit them.

## 3. Web app

- **Configuration → Connection strings:** name `RemoteMySql`, type **MySQL**, value
  `Server=<server>.mysql.database.azure.com;Database=npi;User ID=npi_web;Password=<…>;SslMode=Required`.
  App Service exposes it as `MYSQLCONNSTR_RemoteMySql`, which ASP.NET Core reads as
  `ConnectionStrings:RemoteMySql`.
- **Configuration → Application settings:** `ASPNETCORE_ENVIRONMENT=Production` and
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, so the API rate limiter sees each client's IP instead of the
  App Service front end's. API settings (Stage 5; defaults in `src/Npi.Web/appsettings.json`):
  `Api__PermitLimit` (requests per client IP per window, default 60) and `Api__WindowSeconds` (default 60).
  To require API keys later: `Api__RequireKey=true` plus one or more `Api__Keys__0`, `Api__Keys__1`, …
  (secrets; the site refuses to start when keys are required but none are set).
- **Application Insights:** set `APPLICATIONINSIGHTS_CONNECTION_STRING` (Application settings) to the resource's connection string; without it the site sends nothing. The site sends request paths, status codes, durations, database calls and errors, never query strings, client addresses or browser details (`src/Npi.Web/Telemetry.cs`); keep `/privacy` in step if that changes. Set a daily cap (e.g. 1 GB) on the workspace so a traffic spike can't run up a bill.
- **Configuration → General:** HTTPS only = On, Always On = On, minimum TLS 1.2.
- **Health check:** path `/health`.
- **Custom domain:** optional; add a CNAME + managed certificate once the owner picks a name.

## 4. Loader PC → Azure

> **Superseded (owner, 2026-10-12):** the nightly load will run in Azure on a schedule, not on the loader PC, so the PC does not publish to Azure.
> This section describes the old design and is kept until the Azure phase works out the new one (CLAUDE.md §2 "Azure loading").

On the loader PC, set the publisher connection string as a user secret:

```powershell
dotnet user-secrets --project src/Npi.Loader set "ConnectionStrings:RemoteMySql" "Server=<server>.mysql.database.azure.com;Database=npi;User ID=npi_publish;Password=<…>;SslMode=Required;AllowLoadLocalInfile=true"
```

## 5. Deploying the site (GitHub Actions)

`.github/workflows/ci.yml` builds and tests every PR. The deploy workflow (Stage 6) will publish
`src/Npi.Web` on merge to `master`. Preferred auth is **OIDC federated credentials**:

1. Entra ID → App registrations → New (`github-getnpidata-deploy`).
2. Certificates & secrets → Federated credentials → GitHub Actions, repo `amazedbot/getnpidata`,
   entity *Branch* `master`.
3. Give the app registration the **Website Contributor** role on the web app.
4. GitHub repo → Settings → Secrets and variables → Actions: `AZURE_CLIENT_ID`,
   `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`; variable `AZURE_WEBAPP_NAME`.

Fallback: download the web app's publish profile and store it as secret
`AZURE_WEBAPP_PUBLISH_PROFILE`.

## 6. `az` CLI reference (run only with the owner's OK)

```powershell
$rg = "rg-getnpidata"; $loc = "eastus"; $db = "getnpidata-mysql"; $app = "getnpidata"
$homeIp = "<owner home IP>"

az group create -n $rg -l $loc

az mysql flexible-server create -g $rg -n $db -l $loc `
  --tier Burstable --sku-name Standard_B1ms --version 8.4 `
  --storage-size 32 --storage-auto-grow Enabled --backup-retention 7 `
  --admin-user npiadmin --admin-password "<strong password>" --public-access $homeIp
az mysql flexible-server firewall-rule create -g $rg -n $db --rule-name allow-azure `
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
az mysql flexible-server parameter set -g $rg -s $db -n local_infile --value ON
az mysql flexible-server db create -g $rg -s $db -d npi --charset utf8mb4 --collation utf8mb4_0900_ai_ci

az appservice plan create -g $rg -n plan-getnpidata -l $loc --is-linux --sku B1
az webapp create -g $rg -p plan-getnpidata -n $app --runtime "DOTNETCORE:10.0"
az webapp update -g $rg -n $app --https-only true
az webapp config set -g $rg -n $app --always-on true --min-tls-version 1.2 `
  --generic-configurations '{\"healthCheckPath\": \"/health\"}'
az webapp config connection-string set -g $rg -n $app -t MySql `
  --settings RemoteMySql="Server=$db.mysql.database.azure.com;Database=npi;User ID=npi_web;Password=<…>;SslMode=Required"
```

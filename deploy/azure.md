# Azure setup and deployment

Production runs on **Azure App Service (Linux, .NET 10)** and **Azure Database for MySQL – Flexible
Server**, both in the **same region** (East US unless the owner decides otherwise). The local
loader PC publishes the search projection to the Azure database; the site reads only from it.

> **Provisioning costs money.** The owner creates these resources in the portal, or Claude Code
> runs the `az` commands below **only after the owner says OK** (CLAUDE.md §7 Stage 0.6).
> Open questions (subscription, region, custom domain) are tracked in CLAUDE.md §11 item 2.

## 1. Resources

| Resource | Suggested name | Settings |
|---|---|---|
| Resource group | `rg-getnpidata` | Region: East US |
| MySQL Flexible Server | `getnpidata-mysql` (globally unique) | Burstable **B1ms**, MySQL **8.0**, 32 GB storage with auto-grow, 7-day backups, public access |
| Database | `npi` | `utf8mb4` / `utf8mb4_0900_ai_ci` |
| App Service plan | `plan-getnpidata` | Linux, **B1** |
| Web app | `getnpidata` (globally unique; becomes `<name>.azurewebsites.net`) | Runtime .NET 10, HTTPS only, Always On, health check `/health` |

### Rough monthly cost (East US, pay-as-you-go, Oct 2026 list prices; verify in the Azure pricing calculator)

| Item | ≈ USD / month |
|---|---|
| App Service B1 Linux | 13 |
| MySQL B1ms compute | 12–15 |
| MySQL storage 32 GB | 4 |
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
| `character_set_server` | `utf8mb4` (8.0 default) | |

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
- **Configuration → Application settings:** `ASPNETCORE_ENVIRONMENT=Production`; later
  `Api__RequireKey=false` (Stage 5).
- **Configuration → General:** HTTPS only = On, Always On = On, minimum TLS 1.2.
- **Health check:** path `/health`.
- **Custom domain:** optional; add a CNAME + managed certificate once the owner picks a name.

## 4. Loader PC → Azure

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
  --tier Burstable --sku-name Standard_B1ms --version 8.0.21 `
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

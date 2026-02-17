# digcfo-web04

ASP.NET Core Web API for user statistics from an Azure SQL database.

## Run locally

1. Set the Azure SQL connection string (placeholder for now):

```powershell
$env:ConnectionStrings__StatsDb = "Server=tcp:...;Database=...;User ID=...;Password=...;Encrypt=True;"
```

2. Run the API:

```powershell
dotnet run
```

3. Test the stats endpoints:

```powershell
Invoke-RestMethod http://localhost:5079/stats
Invoke-RestMethod http://localhost:5079/stats/customers
```

Note: `/stats/customers` returns account overview data from the registration and finance databases using the same base connection string.

## Deploy to Azure App Service

### Prerequisites

- Azure CLI installed and logged in (`az login`)
- .NET SDK installed
- A valid Azure SQL connection string for `StatsDb`

### 1) One-command deploy (create + configure + publish)

Run from repo root:

```powershell
.\scripts\Deploy-AzureAppService.ps1 `
	-ResourceGroup "rg-digcfo-prod" `
	-Location "northeurope" `
	-AppServicePlan "asp-digcfo-prod" `
	-WebAppName "digcfo-webapi-prod" `
	-StatsDbConnectionString "Server=tcp:...;Database=...;User ID=...;Password=...;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
```

What this script does:

- Creates/updates Resource Group
- Creates/updates Linux App Service Plan (`DOTNETCORE:8.0`)
- Creates Web App if missing
- Sets app settings:
	- `ASPNETCORE_ENVIRONMENT=Production`
	- `ConnectionStrings__StatsDb=<value>`
- Publishes and deploys this API as ZIP package

### 2) Open Azure SQL firewall for Web App outbound IPs

```powershell
.\scripts\Set-AzureSqlFirewallForWebApp.ps1 `
	-WebAppResourceGroup "rg-digcfo-prod" `
	-WebAppName "digcfo-webapi-prod" `
	-SqlServerResourceGroup "rg-database-prod" `
	-SqlServerName "capassa-digital-cfo-sql"
```

This adds one firewall rule per outbound IP from the Web App.

### 3) Verify in Azure

```powershell
Invoke-RestMethod https://digcfo-webapi-prod.azurewebsites.net/stats
Invoke-RestMethod https://digcfo-webapi-prod.azurewebsites.net/stats/customers
```

## Security note

- Prefer storing secrets in Azure Key Vault and use Key Vault references in App Service for production.
- Avoid committing real connection strings/passwords in `appsettings.json`.

## GitHub Actions (auto deploy)

Workflow is included in `.github/workflows/deploy-azure-webapp.yml`.

### Required GitHub Secrets
- `AZURE_CREDENTIALS` (service principal JSON for `azure/login`)
- `AZURE_RESOURCE_GROUP` (for example `rg-digcfo-prod`)
- `AZURE_WEBAPP_NAME` (for example `digcfo-webapi-prod`)
- `STATSDB_CONNECTIONSTRING` (full SQL connection string)

### How it works
- Triggers on push to `main` (and manual run via `workflow_dispatch`)
- Builds and publishes `DigCfoWebApi.csproj`
- Sets App Service settings (`ASPNETCORE_ENVIRONMENT`, `ConnectionStrings__StatsDb`)
- Deploys ZIP package to Azure Web App

## Manual + CI/CD together
- Use `scripts/Deploy-AzureAppService.ps1` and `scripts/Set-AzureSqlFirewallForWebApp.ps1` for first-time setup.
- Use GitHub Actions workflow for continuous deploy after setup.

## Mest mulig automatisert oppsett (én kommando)

For å automatisere oppsett av service principal + GitHub Secrets, bruk scriptet under:

```powershell
./scripts/Setup-GitHubActionsAzureDeploy.ps1 \
	-ResourceGroup "<resource-group>" \
	-WebAppName "<webapp-name>" \
	-StatsDbConnectionString "Server=tcp:...;Initial Catalog=...;User ID=...;Password=...;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;" \
	-TriggerWorkflow
```

Du kan også kjøre kun:

```powershell
./scripts/Setup-GitHubActionsAzureDeploy.ps1 -TriggerWorkflow
```

Da forsøker scriptet å auto-finne subscription og GitHub repo, og spør bare om manglende verdier.

Scriptet gjør dette:
- Logger inn i Azure CLI (`az`) og GitHub CLI (`gh`) ved behov.
- Oppretter service principal med Contributor på Resource Group-scope.
- Setter GitHub Secrets: `AZURE_CREDENTIALS`, `AZURE_RESOURCE_GROUP`, `AZURE_WEBAPP_NAME`, `STATSDB_CONNECTIONSTRING`.
- Trigger workflowen `Deploy API to Azure App Service` hvis `-TriggerWorkflow` er med.

Krav:
- `az` og `gh` installert.
- `gh auth login` må ha tilgang til repoet.
- Bruker må ha rettigheter til å opprette service principal i Azure AD og rolle-tildeling på subscription/resource group.
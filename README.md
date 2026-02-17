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
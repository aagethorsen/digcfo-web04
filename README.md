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
param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$Location,

    [Parameter(Mandatory = $true)]
    [string]$AppServicePlan,

    [Parameter(Mandatory = $true)]
    [string]$WebAppName,

    [Parameter(Mandatory = $true)]
    [string]$StatsDbConnectionString,

    [string]$SubscriptionId,
    [string]$Sku = "B1"
)

$ErrorActionPreference = "Stop"

function Ensure-AzLogin {
    $null = az account show 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Not logged in to Azure. Opening az login..." -ForegroundColor Yellow
        az login | Out-Null
    }
}

function Ensure-Subscription {
    param([string]$SubId)

    if ([string]::IsNullOrWhiteSpace($SubId)) {
        return
    }

    Write-Host "Setting subscription: $SubId" -ForegroundColor Cyan
    az account set --subscription $SubId
}

function Ensure-ResourceGroup {
    Write-Host "Ensuring resource group '$ResourceGroup' in '$Location'..." -ForegroundColor Cyan
    az group create --name $ResourceGroup --location $Location --output none
}

function Ensure-AppServicePlan {
    Write-Host "Ensuring App Service plan '$AppServicePlan'..." -ForegroundColor Cyan
    az appservice plan create --name $AppServicePlan --resource-group $ResourceGroup --location $Location --sku $Sku --is-linux --output none
}

function Ensure-WebApp {
    Write-Host "Ensuring web app '$WebAppName'..." -ForegroundColor Cyan

    $null = az webapp show --resource-group $ResourceGroup --name $WebAppName 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Web app already exists. Skipping create." -ForegroundColor DarkGray
        return
    }

    az webapp create --resource-group $ResourceGroup --plan $AppServicePlan --name $WebAppName --runtime "DOTNETCORE:8.0" --output none
}

function Configure-AppSettings {
    Write-Host "Configuring app settings..." -ForegroundColor Cyan
    az webapp config appsettings set --resource-group $ResourceGroup --name $WebAppName --settings ASPNETCORE_ENVIRONMENT=Production ConnectionStrings__StatsDb="$StatsDbConnectionString" --output none
}

function Publish-And-Deploy {
    $publishDir = Join-Path $PSScriptRoot "..\.publish"
    $zipPath = Join-Path $PSScriptRoot "..\deploy.zip"

    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }
    if (Test-Path $zipPath) {
        Remove-Item -Force $zipPath
    }

    Write-Host "Publishing .NET API..." -ForegroundColor Cyan
    dotnet publish (Join-Path $PSScriptRoot "..\DigCfoWebApi.csproj") -c Release -o $publishDir

    Write-Host "Creating deployment package..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

    Write-Host "Deploying package to Azure Web App..." -ForegroundColor Cyan
    az webapp deploy --resource-group $ResourceGroup --name $WebAppName --src-path $zipPath --type zip --output none

    Write-Host "Cleaning local deployment artifacts..." -ForegroundColor DarkGray
    Remove-Item -Recurse -Force $publishDir
    Remove-Item -Force $zipPath
}

Ensure-AzLogin
Ensure-Subscription -SubId $SubscriptionId
Ensure-ResourceGroup
Ensure-AppServicePlan
Ensure-WebApp
Configure-AppSettings
Publish-And-Deploy

Write-Host "Deployment complete." -ForegroundColor Green
Write-Host "App URL: https://$WebAppName.azurewebsites.net" -ForegroundColor Green
Write-Host "Health check: https://$WebAppName.azurewebsites.net/stats" -ForegroundColor Green

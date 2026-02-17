param(
    [string]$SubscriptionId,

    [string]$ResourceGroup,

    [string]$WebAppName,

    [string]$StatsDbConnectionString,

    [string]$GitHubRepo,

    [string]$ServicePrincipalName,
    [switch]$TriggerWorkflow
)

$ErrorActionPreference = "Stop"

function Test-RequiredCommand {
    param([string]$Name)

    if (Get-Command $Name -ErrorAction SilentlyContinue) {
        return
    }

    $fallbackPaths = @()
    if ($Name -eq "az") {
        $fallbackPaths = @(
            "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
            "C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
        )
    }
    elseif ($Name -eq "gh") {
        $fallbackPaths = @(
            "C:\Program Files\GitHub CLI\gh.exe"
        )
    }

    $existingPath = $fallbackPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($existingPath) {
        Set-Alias $Name $existingPath -Scope Script
        return
    }

    throw "Required command '$Name' was not found."
}

function Connect-AzureIfNeeded {
    $azAccountExitCode = 0
    try {
        az account show 1>$null 2>$null
        $azAccountExitCode = $LASTEXITCODE
    }
    catch {
        $azAccountExitCode = 1
    }

    if ($azAccountExitCode -ne 0) {
        Write-Host "Not logged in to Azure. Running az login..." -ForegroundColor Yellow
        az login | Out-Null
    }

    az account set --subscription $SubscriptionId
}

function Connect-GitHubIfNeeded {
    $ghAuthExitCode = 0
    try {
        gh auth status 1>$null 2>$null
        $ghAuthExitCode = $LASTEXITCODE
    }
    catch {
        $ghAuthExitCode = 1
    }

    if ($ghAuthExitCode -ne 0) {
        Write-Host "Not logged in to GitHub CLI. Running gh auth login..." -ForegroundColor Yellow
        gh auth login
    }
}

function Get-CurrentSubscriptionId {
    $subscription = az account show --query id -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($subscription)) {
        throw "Unable to resolve current Azure subscription ID."
    }

    return $subscription.Trim()
}

function Get-GitHubRepoFromGitRemote {
    $remoteUrl = git remote get-url origin 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remoteUrl)) {
        return $null
    }

    if ($remoteUrl -match "github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(\.git)?$") {
        return "$($Matches.owner)/$($Matches.repo)"
    }

    return $null
}

function New-ServicePrincipalCredentials {
    param(
        [string]$SpName,
        [string]$Scope
    )

    Write-Host "Creating or updating service principal '$SpName' for scope '$Scope'..." -ForegroundColor Cyan
    $json = az ad sp create-for-rbac --name $SpName --role contributor --scopes $Scope --sdk-auth
    if (-not $json) {
        throw "Failed to create service principal credentials."
    }

    return $json
}

function Set-GitHubSecret {
    param(
        [string]$Repo,
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Secret '$Name' has an empty value."
    }

    Write-Host "Setting GitHub secret '$Name'..." -ForegroundColor Cyan
    $Value | gh secret set $Name --repo $Repo
}

Test-RequiredCommand -Name "az"
Test-RequiredCommand -Name "gh"

Connect-AzureIfNeeded
Connect-GitHubIfNeeded

if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
    $SubscriptionId = Get-CurrentSubscriptionId
    Write-Host "Using subscription: $SubscriptionId" -ForegroundColor DarkCyan
}

if ([string]::IsNullOrWhiteSpace($GitHubRepo)) {
    $GitHubRepo = Get-GitHubRepoFromGitRemote
}

if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $ResourceGroup = Read-Host "Resource Group"
}

if ([string]::IsNullOrWhiteSpace($WebAppName)) {
    $WebAppName = Read-Host "Web App Name"
}

if ([string]::IsNullOrWhiteSpace($GitHubRepo)) {
    $GitHubRepo = Read-Host "GitHub repo (owner/repo)"
}

if ([string]::IsNullOrWhiteSpace($StatsDbConnectionString)) {
    $StatsDbConnectionString = Read-Host "StatsDb connection string"
}

if ([string]::IsNullOrWhiteSpace($ServicePrincipalName)) {
    $safeRepoName = ($GitHubRepo -replace "[^a-zA-Z0-9-]", "-").ToLowerInvariant()
    $ServicePrincipalName = "sp-$safeRepoName-$WebAppName"
}

$scope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
$azureCredentials = New-ServicePrincipalCredentials -SpName $ServicePrincipalName -Scope $scope

Set-GitHubSecret -Repo $GitHubRepo -Name "AZURE_CREDENTIALS" -Value $azureCredentials
Set-GitHubSecret -Repo $GitHubRepo -Name "AZURE_RESOURCE_GROUP" -Value $ResourceGroup
Set-GitHubSecret -Repo $GitHubRepo -Name "AZURE_WEBAPP_NAME" -Value $WebAppName
Set-GitHubSecret -Repo $GitHubRepo -Name "STATSDB_CONNECTIONSTRING" -Value $StatsDbConnectionString

Write-Host "GitHub Secrets are configured for $GitHubRepo." -ForegroundColor Green

if ($TriggerWorkflow.IsPresent) {
    Write-Host "Triggering workflow 'Deploy API to Azure App Service'..." -ForegroundColor Cyan
    gh workflow run "Deploy API to Azure App Service" --repo $GitHubRepo
    Write-Host "Workflow trigger requested." -ForegroundColor Green
}

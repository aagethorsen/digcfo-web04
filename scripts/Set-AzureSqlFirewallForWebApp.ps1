param(
    [Parameter(Mandatory = $true)]
    [string]$WebAppResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$WebAppName,

    [Parameter(Mandatory = $true)]
    [string]$SqlServerResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$SqlServerName,

    [string]$RulePrefix = "webapp-outbound"
)

$ErrorActionPreference = "Stop"

Write-Host "Reading outbound IP addresses from Web App '$WebAppName'..." -ForegroundColor Cyan
$ipsRaw = az webapp show --resource-group $WebAppResourceGroup --name $WebAppName --query outboundIpAddresses --output tsv

if ([string]::IsNullOrWhiteSpace($ipsRaw)) {
    throw "No outbound IP addresses found on Web App."
}

$ips = $ipsRaw.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" } | Select-Object -Unique

if ($ips.Count -eq 0) {
    throw "No valid outbound IP addresses to apply."
}

Write-Host "Applying SQL firewall rules on server '$SqlServerName'..." -ForegroundColor Cyan
$index = 1
foreach ($ip in $ips) {
    $ruleName = "$RulePrefix-$index"
    Write-Host "- $ruleName => $ip" -ForegroundColor DarkGray
    az sql server firewall-rule create --resource-group $SqlServerResourceGroup --server $SqlServerName --name $ruleName --start-ip-address $ip --end-ip-address $ip --output none
    $index++
}

Write-Host "Firewall rules updated." -ForegroundColor Green

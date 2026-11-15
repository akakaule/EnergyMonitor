<#
.SYNOPSIS
    Copies the Eloverblik and Telegram tokens from Key Vault into the web project's user secrets.

.DESCRIPTION
    For a development workstation. Requires `az login` with read access to the vault's secrets.
    The tokens are piped to `dotnet user-secrets` on stdin, so they never appear on a command line,
    and other user secrets are kept. Run it again after a token is renewed in the vault.

.EXAMPLE
    ./scripts/pull-secrets.ps1 -RealUpstreams
#>
param(
    [string] $Vault = 'kv-em-dev-skljlmd6ey75m',
    # Also set UseRealUpstreams=true on the AppHost, so `aspire run` talks to the real services
    # instead of the fakes.
    [switch] $RealUpstreams,
    [string] $Project = (Join-Path $PSScriptRoot '..\src\EnergyMonitor.Web')
)

$ErrorActionPreference = 'Stop'

function Get-VaultSecret([string] $Name) {
    $value = az keyvault secret show --vault-name $Vault --name $Name --query value -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
        throw "Could not read '$Name' from Key Vault '$Vault'. Run 'az login' with an account that can read its secrets."
    }
    return $value.Trim()
}

$secrets = [ordered]@{}
foreach ($name in 'Eloverblik--RefreshToken', 'Telegram--BotToken') {
    $secrets[$name -replace '--', ':'] = Get-VaultSecret $name
}

$secrets | ConvertTo-Json -Compress | dotnet user-secrets set --project $Project | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed.' }
$secrets.Clear()

if ($RealUpstreams) {
    dotnet user-secrets set --project (Join-Path $PSScriptRoot '..\src\EnergyMonitor.AppHost') UseRealUpstreams true | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed.' }
}

Write-Host "Stored Eloverblik:RefreshToken and Telegram:BotToken from '$Vault' in the user secrets of $(Split-Path -Leaf (Resolve-Path $Project))."
if ($RealUpstreams) { Write-Host 'Set UseRealUpstreams=true on the AppHost.' }

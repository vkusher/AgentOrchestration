# Launches all local services in separate PowerShell windows.
# - MCP server (web)        http://localhost:56159
# - API                     http://localhost:5080
# - Customer chat UI        http://localhost:5173
# - Reviewer UI             http://localhost:5174
#
# Usage:  pwsh -File .\scripts\run-local.ps1
#         pwsh -File .\scripts\run-local.ps1 -Stop      # kill anything on those ports
#         pwsh -File .\scripts\run-local.ps1 -NoWeb     # skip the React UIs
#
# Note: Reviewer UI requires `npm install` to have been run once in src/AgentHandoff.Reviewer.

[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$NoWeb,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$ports = @{
    Mcp      = 56159
    Api      = 5080
    Web      = 5173
    Reviewer = 5174
}

function Stop-Port {
    param([int]$Port)
    $pids = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($procId in $pids) {
        try {
            Stop-Process -Id $procId -Force -ErrorAction Stop
            Write-Host "  killed PID $procId on :$Port"
        } catch {
            Write-Host "  (could not kill PID $procId on :$Port — $($_.Exception.Message))"
        }
    }
}

function Start-InWindow {
    param(
        [string]$Title,
        [string]$WorkDir,
        [string]$Command
    )
    $escaped = $Command -replace "'", "''"
    $args = "-NoExit -NoProfile -Command `"`$Host.UI.RawUI.WindowTitle = '$Title'; Set-Location -LiteralPath '$WorkDir'; $escaped`""
    Start-Process -FilePath 'pwsh' -ArgumentList $args | Out-Null
}

if ($Stop) {
    Write-Host "Stopping local services..."
    foreach ($name in $ports.Keys) { Stop-Port -Port $ports[$name] }
    return
}

Write-Host "Stopping anything currently bound to required ports..."
foreach ($name in $ports.Keys) { Stop-Port -Port $ports[$name] }

Write-Host "Starting MCP server (web) on http://localhost:$($ports.Mcp)..."
Start-InWindow -Title 'MCP Server' -WorkDir $repoRoot `
    -Command "dotnet run --project src/AgentHandoff.McpServerWeb -c $Configuration"

Write-Host "Starting API on http://localhost:$($ports.Api)..."
Start-InWindow -Title 'API' -WorkDir $repoRoot `
    -Command "dotnet run --project src/AgentHandoff.Api -c $Configuration"

if (-not $NoWeb) {
    $webDir      = Join-Path $repoRoot 'src/AgentHandoff.Web'
    $reviewerDir = Join-Path $repoRoot 'src/AgentHandoff.Reviewer'

    foreach ($d in @($webDir, $reviewerDir)) {
        if (-not (Test-Path (Join-Path $d 'node_modules'))) {
            Write-Host "Installing npm packages in $d ..."
            Push-Location $d
            try { npm install --no-audit --no-fund } finally { Pop-Location }
        }
    }

    Write-Host "Starting customer chat UI on http://localhost:$($ports.Web)..."
    Start-InWindow -Title 'Web (chat)' -WorkDir $webDir -Command 'npm run dev'

    Write-Host "Starting reviewer UI on http://localhost:$($ports.Reviewer)..."
    Start-InWindow -Title 'Reviewer' -WorkDir $reviewerDir -Command 'npm run dev'
}

Write-Host ""
Write-Host "All services launched in separate windows:"
Write-Host "  MCP        http://localhost:$($ports.Mcp)/health"
Write-Host "  API        http://localhost:$($ports.Api)/health"
if (-not $NoWeb) {
    Write-Host "  Chat UI    http://localhost:$($ports.Web)"
    Write-Host "  Reviewer   http://localhost:$($ports.Reviewer)"
}
Write-Host ""
Write-Host "Stop everything later with:  pwsh -File .\scripts\run-local.ps1 -Stop"

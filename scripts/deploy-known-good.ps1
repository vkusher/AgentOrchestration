[CmdletBinding()]
param(
    [string]$Subscription = "",
    [string]$ResourceGroup = "",
    [string]$ApiApp = "",
    [string]$McpApp = "",
    [string]$WebApp = "",
    [string]$ApiBaseUrl = "",
    [string]$McpBaseUrl = "",
    [string]$WebBaseUrl = "",

    # Money-transfer extractor backend (Document Intelligence + Azure OpenAI for the MCP web app).
    # Endpoints are baked in; KEYS must be supplied via env vars or parameters at run time.
    [string]$DocIntelEndpoint = "",
    [string]$DocIntelApiKey   = $env:DOCINTEL_API_KEY,
    [string]$TransferAoaiEndpoint        = $env:TRANSFER_AOAI_ENDPOINT,
    [string]$TransferAoaiApiKey          = $env:TRANSFER_AOAI_API_KEY,
    [string]$TransferAoaiDeploymentName  = $env:TRANSFER_AOAI_DEPLOYMENT
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outDir = Join-Path $repoRoot ".out"
if (!(Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Write-Host "==> $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

function Get-HttpCode {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,
        [string]$Method = "GET",
        [string]$Body,
        [hashtable]$Headers = @{},
        [int]$MaxTime = 60
    )

    $headerArgs = @()
    foreach ($key in $Headers.Keys) {
        $headerArgs += "-H"
        $headerArgs += ("{0}: {1}" -f $key, $Headers[$key])
    }

    $args = @(
        "-sS", "-o", "NUL", "-w", "%{http_code}",
        "--retry", "20", "--retry-delay", "2", "--retry-all-errors",
        "--max-time", "$MaxTime", "-X", $Method
    ) + $headerArgs

    if (![string]::IsNullOrWhiteSpace($Body)) {
        $args += "--data"
        $args += $Body
    }

    $args += $Url

    return (curl.exe @args).Trim()
}

function Get-HttpBody {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,
        [string]$Method = "GET",
        [string]$Body,
        [hashtable]$Headers = @{},
        [int]$MaxTime = 60
    )

    $headerArgs = @()
    foreach ($key in $Headers.Keys) {
        $headerArgs += "-H"
        $headerArgs += ("{0}: {1}" -f $key, $Headers[$key])
    }

    $args = @(
        "-sS",
        "--retry", "20", "--retry-delay", "2", "--retry-all-errors",
        "--max-time", "$MaxTime", "-X", $Method
    ) + $headerArgs

    if (![string]::IsNullOrWhiteSpace($Body)) {
        $args += "--data"
        $args += $Body
    }

    $args += $Url

    return (curl.exe @args)
}

Invoke-Checked -Name "Set Azure subscription" -Action {
    az account set --subscription $Subscription
}

Invoke-Checked -Name "Publish MCP web app" -Action {
    dotnet publish (Join-Path $repoRoot "src/AgentHandoff.McpServerWeb/AgentHandoff.McpServerWeb.csproj") -c Release -o (Join-Path $outDir "mcpweb-publish") /p:ErrorOnDuplicatePublishOutputFiles=false
}

$mcpZip = Join-Path $outDir "mcpweb-tar.zip"
if (Test-Path $mcpZip) {
    Remove-Item $mcpZip -Force
}

Push-Location (Join-Path $outDir "mcpweb-publish")
try {
    Invoke-Checked -Name "Package MCP web app (tar zip)" -Action {
        tar -a -c -f "../mcpweb-tar.zip" .
    }
}
finally {
    Pop-Location
}

Invoke-Checked -Name "Deploy MCP web app" -Action {
    az webapp deploy --resource-group $ResourceGroup --name $McpApp --src-path $mcpZip --type zip
}

Invoke-Checked -Name "Set MCP startup command" -Action {
    az webapp config set --resource-group $ResourceGroup --name $McpApp --startup-file "dotnet /home/site/wwwroot/AgentHandoff.McpServerWeb.dll"
}

# Push DocumentIntelligence + AzureOpenAI settings so the money-transfer extractor wakes up.
# When TransferAoaiEndpoint is empty, fall back to the API app's existing AzureOpenAI__* values.
if ([string]::IsNullOrWhiteSpace($TransferAoaiEndpoint)) {
    Write-Host "==> TRANSFER_AOAI_ENDPOINT not set; pulling AzureOpenAI__* from API app for the MCP web app."
    $apiAoai = az webapp config appsettings list --resource-group $ResourceGroup --name $ApiApp --output json | ConvertFrom-Json
    $TransferAoaiEndpoint       = ($apiAoai | Where-Object { $_.name -eq 'AzureOpenAI__Endpoint'       }).value
    $TransferAoaiApiKey         = ($apiAoai | Where-Object { $_.name -eq 'AzureOpenAI__ApiKey'         }).value
    $TransferAoaiDeploymentName = ($apiAoai | Where-Object { $_.name -eq 'AzureOpenAI__DeploymentName' }).value
}

if (-not [string]::IsNullOrWhiteSpace($DocIntelApiKey)) {
    Invoke-Checked -Name "Set MCP DocumentIntelligence settings" -Action {
        az webapp config appsettings set --resource-group $ResourceGroup --name $McpApp --settings `
            "DocumentIntelligence__Endpoint=$DocIntelEndpoint" `
            "DocumentIntelligence__ApiKey=$DocIntelApiKey" `
            --output none
    }
} else {
    Write-Warning "DOCINTEL_API_KEY not provided; skipping DocumentIntelligence settings on MCP app."
}

if (-not [string]::IsNullOrWhiteSpace($TransferAoaiEndpoint) -and -not [string]::IsNullOrWhiteSpace($TransferAoaiApiKey)) {
    Invoke-Checked -Name "Set MCP AzureOpenAI settings (for transfer extractor)" -Action {
        az webapp config appsettings set --resource-group $ResourceGroup --name $McpApp --settings `
            "AzureOpenAI__Endpoint=$TransferAoaiEndpoint" `
            "AzureOpenAI__ApiKey=$TransferAoaiApiKey" `
            "AzureOpenAI__DeploymentName=$TransferAoaiDeploymentName" `
            --output none
    }
} else {
    Write-Warning "AzureOpenAI endpoint/key not resolved; transfer extractor will use the regex fallback."
}

Invoke-Checked -Name "Restart MCP app" -Action {
    az webapp restart --resource-group $ResourceGroup --name $McpApp
}

Invoke-Checked -Name "Publish API app" -Action {
    dotnet publish (Join-Path $repoRoot "src/AgentHandoff.Api/AgentHandoff.Api.csproj") -c Release -o (Join-Path $outDir "api-publish")
}

$apiZip = Join-Path $outDir "api-tar.zip"
if (Test-Path $apiZip) {
    Remove-Item $apiZip -Force
}

Push-Location (Join-Path $outDir "api-publish")
try {
    Invoke-Checked -Name "Package API app (tar zip)" -Action {
        tar -a -c -f "../api-tar.zip" .
    }
}
finally {
    Pop-Location
}

Invoke-Checked -Name "Clear API run-from-package setting" -Action {
    # Linux App Service mounts the zip as squashfs when WEBSITE_RUN_FROM_PACKAGE=1.
    # The API package is large enough to hit VolumeMountFailure / BadRunFromPackageConfig,
    # so we extract via Kudu zipdeploy instead. Ignore failure if the setting is absent.
    az webapp config appsettings delete --resource-group $ResourceGroup --name $ApiApp --setting-names WEBSITE_RUN_FROM_PACKAGE --output none
    $script:LASTEXITCODE = 0
}

Invoke-Checked -Name "Deploy API app (Kudu zipdeploy / extract)" -Action {
    az webapp deployment source config-zip --resource-group $ResourceGroup --name $ApiApp --src $apiZip --timeout 900
}

Invoke-Checked -Name "Set API startup command" -Action {
    az webapp config set --resource-group $ResourceGroup --name $ApiApp --startup-file "dotnet /home/site/wwwroot/AgentHandoff.Api.dll"
}

Invoke-Checked -Name "Set API MCP remote settings" -Action {
    az webapp config appsettings set --resource-group $ResourceGroup --name $ApiApp --settings "Mcp__Mode=Remote" "Mcp__ServerPath=$McpBaseUrl" "Mcp__ServerDllPath=" --output none
}

Invoke-Checked -Name "Restart API app" -Action {
    az webapp restart --resource-group $ResourceGroup --name $ApiApp
}

# ---------------------------------------------------------------------------
# Web app (Azure Static Web Apps) — build Vite bundle and push via SWA CLI
# ---------------------------------------------------------------------------
$webDir = Join-Path $repoRoot "src/AgentHandoff.Web"
$webDist = Join-Path $webDir "dist"

# Bake the API base URL into the production bundle so the static site can call the API.
$envProdPath = Join-Path $webDir ".env.production"
$envProdLine = "VITE_API_BASE_URL=$ApiBaseUrl"
Set-Content -Path $envProdPath -Value $envProdLine -Encoding UTF8

Push-Location $webDir
try {
    if (-not (Test-Path (Join-Path $webDir "node_modules"))) {
        Invoke-Checked -Name "Install web npm packages" -Action {
            npm ci --no-audit --no-fund 2>&1 | Out-Host
        }
    }

    Invoke-Checked -Name "Build web bundle (vite production)" -Action {
        npm run build 2>&1 | Out-Host
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path (Join-Path $webDist "index.html"))) {
    throw "Web build did not produce dist/index.html at $webDist"
}

# Pull the deployment token from the SWA so we don't need an extra auth step.
Write-Host "==> Get SWA deployment token"
$swaToken = az staticwebapp secrets list --name $WebApp --resource-group $ResourceGroup --query "properties.apiKey" -o tsv
if ([string]::IsNullOrWhiteSpace($swaToken)) {
    throw "Could not retrieve deployment token for SWA '$WebApp' in '$ResourceGroup'."
}

Invoke-Checked -Name "Deploy web bundle to Static Web App ($WebApp)" -Action {
    # SWA CLI is run via npx so we don't require a global install.
    npx -y @azure/static-web-apps-cli@latest deploy $webDist `
        --deployment-token $swaToken `
        --env production `
        --no-use-keychain 2>&1 | Out-Host
}

# Make sure the API allows the SWA origin (and keep localhost dev origins for parity).
$swaOrigin = "https://$(([uri]$WebBaseUrl).Host)"
Invoke-Checked -Name "Allow SWA origin in API CORS" -Action {
    az webapp config appsettings set --resource-group $ResourceGroup --name $ApiApp --settings `
        "Cors__AllowedOrigins__0=$swaOrigin" `
        "Cors__AllowedOrigins__1=http://localhost:5173" `
        "Cors__AllowedOrigins__2=http://localhost:4173" `
        --output none
}

Invoke-Checked -Name "Restart API app (post-CORS)" -Action {
    az webapp restart --resource-group $ResourceGroup --name $ApiApp
}

# Probes
$MCP_HEALTH_CODE = Get-HttpCode -Url "$McpBaseUrl/health"
$MCP_TOOLS_CODE = Get-HttpCode -Url "$McpBaseUrl/mcp/tools"
$mcpExecuteBody = '{"toolName":"SearchKnowledgeBase","arguments":{"query":"business hours"}}'
$MCP_EXECUTE_CODE = Get-HttpCode -Url "$McpBaseUrl/mcp/execute" -Method "POST" -Headers @{"Content-Type" = "application/json"} -Body $mcpExecuteBody

$API_HEALTH_CODE = Get-HttpCode -Url "$ApiBaseUrl/health"
$API_MODE = "<unavailable>"
$API_TOOLCOUNT = "<unavailable>"

try {
    $debugRaw = Get-HttpBody -Url "$ApiBaseUrl/api/debug/mcp"
    $debugObj = $debugRaw | ConvertFrom-Json
    if ($null -ne $debugObj) {
        if ($null -ne $debugObj.mcpMode) { $API_MODE = [string]$debugObj.mcpMode }
        if ($null -ne $debugObj.lastMcpToolCount) { $API_TOOLCOUNT = [string]$debugObj.lastMcpToolCount }
    }
}
catch {
}

$chatBody = '{"message":"What are your branch opening hours?","sessionId":"deploy-smoke","mode":"handoff"}'
$chatOutputPath = Join-Path $outDir "chat_probe.txt"
if (Test-Path $chatOutputPath) {
    Remove-Item $chatOutputPath -Force
}

$CHAT_STATUS = (curl.exe -sS --max-time 50 -o $chatOutputPath -w "%{http_code}" --retry 5 --retry-delay 2 --retry-all-errors -H "Content-Type: application/json" -H "Accept: text/event-stream" -X POST "$ApiBaseUrl/api/chat/stream" --data $chatBody).Trim()

$chatContent = ""
if (Test-Path $chatOutputPath) {
    $chatContent = Get-Content $chatOutputPath -Raw
}

$CHAT_HAS_TOOL_CALL = $chatContent.Contains("tool_call")
$CHAT_HAS_TOOL_RESULT = $chatContent.Contains("tool_result")
$CHAT_HAS_SEARCH = $chatContent.Contains("SearchKnowledgeBase")

# Web (SWA) probes
$WEB_HOME_CODE = Get-HttpCode -Url "$WebBaseUrl/" -MaxTime 30
$WEB_PREFLIGHT_CODE = (curl.exe -sS -o NUL -w "%{http_code}" --max-time 30 `
    -H "Origin: $WebBaseUrl" `
    -H "Access-Control-Request-Method: POST" `
    -H "Access-Control-Request-Headers: content-type" `
    -X OPTIONS "$ApiBaseUrl/api/chat/stream").Trim()

$OVERALL = "PARTIAL"
if (
    $MCP_HEALTH_CODE -eq "200" -and
    $MCP_TOOLS_CODE -eq "200" -and
    $MCP_EXECUTE_CODE -eq "200" -and
    $API_HEALTH_CODE -eq "200" -and
    $CHAT_STATUS -eq "200" -and
    $CHAT_HAS_TOOL_CALL -and
    $CHAT_HAS_TOOL_RESULT -and
    $CHAT_HAS_SEARCH -and
    $WEB_HOME_CODE -eq "200" -and
    ($WEB_PREFLIGHT_CODE -eq "200" -or $WEB_PREFLIGHT_CODE -eq "204")
) {
    $OVERALL = "SUCCESS"
}

Write-Output ("MCP_HEALTH_CODE={0}" -f $MCP_HEALTH_CODE)
Write-Output ("MCP_TOOLS_CODE={0}" -f $MCP_TOOLS_CODE)
Write-Output ("MCP_EXECUTE_CODE={0}" -f $MCP_EXECUTE_CODE)
Write-Output ("API_HEALTH_CODE={0}" -f $API_HEALTH_CODE)
Write-Output ("API_MODE={0}" -f $API_MODE)
Write-Output ("API_TOOLCOUNT={0}" -f $API_TOOLCOUNT)
Write-Output ("CHAT_STATUS={0}" -f $CHAT_STATUS)
Write-Output ("CHAT_HAS_TOOL_CALL={0}" -f $CHAT_HAS_TOOL_CALL)
Write-Output ("CHAT_HAS_TOOL_RESULT={0}" -f $CHAT_HAS_TOOL_RESULT)
Write-Output ("CHAT_HAS_SEARCH={0}" -f $CHAT_HAS_SEARCH)
Write-Output ("WEB_HOME_CODE={0}" -f $WEB_HOME_CODE)
Write-Output ("WEB_PREFLIGHT_CODE={0}" -f $WEB_PREFLIGHT_CODE)
Write-Output ("WEB_URL={0}" -f $WebBaseUrl)
Write-Output ("OVERALL={0}" -f $OVERALL)

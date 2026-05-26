targetScope = 'resourceGroup'

@description('Short deployment prefix used for all new Azure resources.')
param prefix string = 'agenticdemo'

@description('Azure region used for the App Service plan and API app.')
param location string = resourceGroup().location

@description('Name of the Linux App Service plan for the API host.')
param appServicePlanName string = '${prefix}-${replace(resourceGroup().name, '-', '')}-asp'

@description('Name of the App Service instance that hosts the API and MCP server process.')
param apiAppName string = '${prefix}-${replace(resourceGroup().name, '-', '')}-api'

@description('Name of the Static Web App that hosts the React frontend.')
param staticWebAppName string = '${prefix}-${replace(resourceGroup().name, '-', '')}-web'

@description('Optional deployment slot settings for the API host.')
param enableAlwaysOn bool = true

// The API host runs the ASP.NET Core API and spawns the MCP server in-process.
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: 'B1'
    tier: 'Basic'
    size: 'B1'
    capacity: 1
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource apiApp 'Microsoft.Web/sites@2023-12-01' = {
  name: apiAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    httpsOnly: true
    serverFarmId: appServicePlan.id
    siteConfig: {
      alwaysOn: enableAlwaysOn
      linuxFxVersion: 'DOTNETCORE|8.0'
      appSettings: [
        // Required for Linux App Service code deployment of ASP.NET Core apps.
        {
          name: 'ASPNETCORE_URLS'
          value: 'http://0.0.0.0:8080'
        }
        {
          name: 'WEBSITES_PORT'
          value: '8080'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
      ]
    }
  }
}

// Frontend hosting. The build will inject the API base URL at compile time.
resource staticWebApp 'Microsoft.Web/staticSites@2024-04-01' = {
  name: staticWebAppName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    allowConfigFileUpdates: true
  }
}

output appServiceDefaultHostName string = apiApp.properties.defaultHostName
output appServiceUrl string = 'https://${apiApp.properties.defaultHostName}'
output staticWebAppDefaultHostName string = staticWebApp.properties.defaultHostname
output staticWebAppUrl string = 'https://${staticWebApp.properties.defaultHostname}'

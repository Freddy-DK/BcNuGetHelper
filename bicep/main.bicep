// Resource-group scoped deployment for BcNuGetHelper.
// The resource group is created up front by bootstrap.bicep (run once locally).
targetScope = 'resourceGroup'

@minLength(3)
@maxLength(17)
@description('Base name for all resources. Lowercase letters and digits only, 3-17 characters, globally unique.')
param baseName string

@description('Azure region to deploy to.')
param location string = resourceGroup().location

@description('Comma-separated list of feeds (apps, runtime, symbols) served without authentication. Empty means all feeds are private.')
param publicFeeds string = ''

@description('Client (application) ID allowed to call the admin endpoints (upload, access keys) with an Entra bearer token. Empty allows any caller from the tenant with a valid token for the allowed audiences.')
param adminClientId string = ''

@description('GitHub App client ID used to dispatch the runtime-generation workflow. Empty disables runtime generation.')
param githubAppClientId string = ''

@description('Installation ID of the GitHub App on the target repository.')
param githubAppInstallationId string = ''

@description('Target repository for the runtime workflow, in owner/repo form.')
param githubRepo string = ''

@description('Workflow file name to dispatch.')
param githubWorkflowFile string = 'generate-runtime-nuget.yml'

@description('Git ref (branch) the workflow is dispatched on.')
param githubRef string = 'main'

@secure()
@description('GitHub App private key (PEM) used to sign the workflow-dispatch token. Stored as a Function App setting.')
param githubAppPrivateKey string = ''

@description('Comma-separated GitHub logins allowed to use the token-management web app (from the WEBAPPUSERS variable). Empty disables the web app.')
param webAppUsers string = ''

@description('GitHub OAuth App client ID used by the web app for the sign-in device flow. Empty falls back to personal-access-token sign-in only.')
param gitHubOAuthClientId string = ''

// Storage Blob Data Owner (not Contributor) is required by the Functions host for
// identity-based AzureWebJobsStorage (host keys/secrets management).
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: baseName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource packagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'packages'
  properties: {
    publicAccess: 'None'
  }
}

resource deploymentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'deployments'
  properties: {
    publicAccess: 'None'
  }
}

// Holds the access key registry (config/accesskeys.json), managed by the function app.
resource configContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'config'
  properties: {
    publicAccess: 'None'
  }
}

resource funcIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-id'
  location: location
}

// Gives the function app access to packages and deployment artifacts.
resource funcStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, funcIdentity.id, storageBlobDataOwnerRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: funcIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource logWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-log'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logWorkspace.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${baseName}-plan'
  location: location
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'functionapp'
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${baseName}-func'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${funcIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentsContainer.name}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: funcIdentity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'PackagesStorageAccountName'
          value: storage.name
        }
        {
          name: 'PublicFeeds'
          value: publicFeeds
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: funcIdentity.properties.clientId
        }
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storage.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: funcIdentity.properties.clientId
        }
        // Microsoft Entra authorization for the admin endpoints (upload, access keys),
        // in place of Azure Functions host keys. Callers present an Entra bearer token.
        {
          name: 'AdminAuth__TenantId'
          value: subscription().tenantId
        }
        {
          name: 'AdminAuth__AllowedAudiences'
          #disable-next-line no-hardcoded-env-urls
          value: 'https://management.core.windows.net/,https://management.azure.com/,https://management.azure.com'
        }
        {
          name: 'AdminAuth__AllowedClientIds'
          value: adminClientId
        }
        // GitHub App used to dispatch the runtime-generation workflow. The private key is
        // held as an app setting (encrypted at rest, readable with config access to the app).
        {
          name: 'GitHubApp__ClientId'
          value: githubAppClientId
        }
        {
          name: 'GitHubApp__InstallationId'
          value: githubAppInstallationId
        }
        {
          name: 'GitHubApp__Repo'
          value: githubRepo
        }
        {
          name: 'GitHubApp__WorkflowFile'
          value: githubWorkflowFile
        }
        {
          name: 'GitHubApp__Ref'
          value: githubRef
        }
        {
          name: 'GitHubApp__PrivateKey'
          value: githubAppPrivateKey
        }
        // Token-management web app (served from /api/app). WebAppUsers is the GitHub login
        // allow-list; GitHubOAuth__ClientId enables the browser device-flow sign-in.
        {
          name: 'WebAppUsers'
          value: webAppUsers
        }
        {
          name: 'GitHubOAuth__ClientId'
          value: gitHubOAuthClientId
        }
      ]
    }
  }
  dependsOn: [
    funcStorageRole
  ]
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output storageAccountName string = storage.name

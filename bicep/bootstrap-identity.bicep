// RG-scoped resources for the GitHub OIDC deploy identity.
// Called by bootstrap.bicep; not deployed on its own.
targetScope = 'resourceGroup'

@description('Name of the user-assigned identity used by GitHub Actions for OIDC login.')
param deployIdentityName string

@description('Classic GitHub OIDC subject: repo:<owner>/<repo>:ref:refs/heads/main')
param githubSubjectClassic string

@description('ID-based GitHub OIDC subject: repo:<owner>@<ownerId>/<repo>@<repoId>:ref:refs/heads/main')
param githubSubjectWithIds string

// Contributor: create resources. RBAC Administrator: create the role assignment for the
// function's managed identity. Storage Blob Data Contributor: function content deployment
// to the deployments container.
var roleDefinitionIds = {
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
  rbacAdministrator: 'f58310d9-a9f6-439a-9e8d-f62e7b41a168'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: deployIdentityName
  location: resourceGroup().location
}

resource fedClassic 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: 'github-main'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubSubjectClassic
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

// GitHub is rolling out an OIDC subject that embeds account and repository IDs; register
// both so login works either way. Serialized after fedClassic (identity allows one
// federated-credential write at a time).
resource fedWithIds 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: 'github-main-ids'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubSubjectWithIds
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
  dependsOn: [
    fedClassic
  ]
}

resource contributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, roleDefinitionIds.contributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionIds.contributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource rbacAdminRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, roleDefinitionIds.rbacAdministrator)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionIds.rbacAdministrator)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, roleDefinitionIds.storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionIds.storageBlobDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output deployClientId string = identity.properties.clientId
output deployPrincipalId string = identity.properties.principalId

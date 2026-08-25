// Subscription-scoped bootstrap for a BcNuGetHelper fork: creates the deployment resource
// group and the GitHub OIDC deploy identity (with federated credentials and role
// assignments). Run ONCE locally with your own credentials before the Deploy workflow:
//
//   az deployment sub create --location <loc> --template-file bicep/bootstrap.bicep \
//     --parameters baseName=<base> location=<loc> \
//       githubSubjectClassic="repo:<owner>/<repo>:ref:refs/heads/main" \
//       githubSubjectWithIds="repo:<owner>@<id>/<repo>@<id>:ref:refs/heads/main"
targetScope = 'subscription'

@minLength(3)
@maxLength(17)
@description('Base name for all resources. Lowercase letters and digits only, 3-17 characters, globally unique.')
param baseName string

@description('Azure region to deploy to.')
param location string

@description('Name of the resource group to create and deploy into.')
param resourceGroupName string = '${baseName}-rg'

@description('Name of the user-assigned identity used by GitHub Actions for OIDC login.')
param deployIdentityName string = 'github-deploy'

@description('Classic GitHub OIDC subject: repo:<owner>/<repo>:ref:refs/heads/main')
param githubSubjectClassic string

@description('ID-based GitHub OIDC subject: repo:<owner>@<ownerId>/<repo>@<repoId>:ref:refs/heads/main')
param githubSubjectWithIds string

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
}

module identity 'bootstrap-identity.bicep' = {
  name: 'deploy-identity'
  scope: rg
  params: {
    deployIdentityName: deployIdentityName
    githubSubjectClassic: githubSubjectClassic
    githubSubjectWithIds: githubSubjectWithIds
  }
}

output resourceGroupName string = rg.name
output deployClientId string = identity.outputs.deployClientId
output tenantId string = subscription().tenantId
output subscriptionId string = subscription().subscriptionId

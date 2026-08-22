# BcNuGetHelper

A self-hosted NuGet feed service for Business Central apps. Fork this repository and deploy it to your own Azure subscription directly from your fork — no separate deployment repository needed.

The service consists of:

- An **Azure Function** (.NET 10 isolated, Flex Consumption) hosting the endpoints
- A **Storage Account** where packages are stored in a NuGet-friendly folder structure
- A **User-Assigned Managed Identity** granting the function access to the storage account

## Endpoints

All apps are uploaded once and served through three read-only NuGet v3 feeds:

| Feed | Service index URL | Content |
|------|-------------------|---------|
| `apps` | `https://<functionapp>.azurewebsites.net/api/apps/index.json` | Full .app files |
| `runtime` | `https://<functionapp>.azurewebsites.net/api/runtime/index.json` | Runtime packages (transformation added during upload — coming later) |
| `symbols` | `https://<functionapp>.azurewebsites.net/api/symbols/index.json` | Symbols only |

Each feed implements the NuGet v3 resources needed by [BcContainerHelper](https://github.com/microsoft/navcontainerhelper)'s NuGet search functionality:

- **Service index** — `GET api/{feed}/index.json`
- **SearchQueryService** — `GET api/{feed}/query?q=<query>&skip=<n>&take=<n>`
- **PackageBaseAddress (flat container)** —
  - `GET api/{feed}/package/{id}/index.json` (version list)
  - `GET api/{feed}/package/{id}/{version}/{id}.{version}.nupkg` (download)

There is no NuGet push/publish support. All uploads go through the upload endpoint:

- **Upload** — `POST api/upload` (requires a [function key](https://learn.microsoft.com/azure/azure-functions/function-keys-how-to) via `x-functions-key` header or `?code=` query parameter). Accepts a raw `.app` file body or `multipart/form-data` with one or more `.app` files (Business Central apps and their dependencies).

Uploaded apps are parsed (id, name, publisher, version, dependencies from the app manifest), wrapped as NuGet packages with dependency information, and stored under `{feed}/{packageId}/{version}/` in the `packages` blob container.

### Using the feeds with BcContainerHelper

```powershell
$feedUrl = "https://<functionapp>.azurewebsites.net/api/apps/index.json"
Get-BcNuGetPackage -nuGetServerUrl $feedUrl -packageName "<publisher>.<appname>" -select Exact
```

Or register as a trusted feed:

```powershell
$bcContainerHelperConfig.TrustedNuGetFeeds = @(
    @{ "Url" = "https://<functionapp>.azurewebsites.net/api/apps/index.json"; "Token" = "" }
)
```

### Uploading apps

```powershell
$functionKey = "<your function key>"
Invoke-RestMethod `
    -Method Post `
    -Uri "https://<functionapp>.azurewebsites.net/api/upload" `
    -Headers @{ "x-functions-key" = $functionKey } `
    -InFile ".\MyApp_1.0.0.0.app" `
    -ContentType "application/octet-stream"
```

## Deployment

Deployment runs entirely from your fork using GitHub Actions and Terraform. Terraform state is stored in an Azure storage account that the workflow bootstraps automatically.

### 1. Fork this repository

### 2. Create a managed identity for GitHub OIDC

The deploy workflow authenticates with a **user-assigned managed identity** using GitHub OIDC federation — no app registration and no stored credentials. All roles are scoped to the deployment resource group only, so the identity (and the resource group) must be created up front:

```powershell
$github = "<your-github-user>"
$rg     = "<RESOURCE_GROUP_NAME>"          # e.g. mybcnuget-rg
$sub    = az account show --query id -o tsv

az group create --name $rg --location westeurope
az identity create --name github-deploy --resource-group $rg
az identity federated-credential create `
    --identity-name github-deploy `
    --resource-group $rg `
    --name github-main `
    --issuer "https://token.actions.githubusercontent.com" `
    --subject "repo:$github/BcNuGetHelper:ref:refs/heads/main" `
    --audiences "api://AzureADTokenExchange"

$principalId = az identity show --name github-deploy --resource-group $rg --query principalId -o tsv
az role assignment create --assignee $principalId --role "Contributor" --scope "/subscriptions/$sub/resourceGroups/$rg"
az role assignment create --assignee $principalId --role "Role Based Access Control Administrator" --scope "/subscriptions/$sub/resourceGroups/$rg"
az role assignment create --assignee $principalId --role "Storage Blob Data Contributor" --scope "/subscriptions/$sub/resourceGroups/$rg"
```

Roles: **Contributor** (create resources), **Role Based Access Control Administrator** (create the role assignment for the function's managed identity) and **Storage Blob Data Contributor** (read/write Terraform state).

### 3. Configure repository settings

All settings are configured as repository **secrets** and **variables** (Settings → Secrets and variables → Actions).

#### Secrets

| Secret | Required | Description |
|--------|----------|-------------|
| `AZURE_CLIENT_ID` | Yes | Client id of the managed identity created above (`az identity show --name github-deploy --resource-group <rg> --query clientId -o tsv`) |
| `AZURE_TENANT_ID` | Yes | Your Entra ID tenant id |
| `AZURE_SUBSCRIPTION_ID` | Yes | The Azure subscription to deploy to |

#### Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `BASE_NAME` | Yes | — | Base name for all Azure resources. Lowercase letters and digits only, 3–17 characters, globally unique (used for storage account names). Example: `mybcnuget` |
| `AZURE_LOCATION` | No | `westeurope` | Azure region to deploy to |
| `RESOURCE_GROUP_NAME` | No | `<BASE_NAME>-rg` | Name of the resource group (must match the one created in step 2) |

### 4. Deploy

Push to `main` or run the **Deploy** workflow manually (Actions → Deploy → Run workflow). The workflow:

1. Logs in to Azure using OIDC (no stored credentials)
2. Bootstraps the resource group and Terraform state storage (`<BASE_NAME>state`)
3. Runs `terraform apply` to create/update all resources
4. Builds the .NET 10 function app and deploys it

### Resources created

| Resource | Name |
|----------|------|
| Resource group | `<RESOURCE_GROUP_NAME>` |
| Storage account (packages + deployments) | `<BASE_NAME>` |
| Storage account (Terraform state) | `<BASE_NAME>state` |
| User-assigned managed identity | `<BASE_NAME>-id` |
| App Service plan (Flex Consumption) | `<BASE_NAME>-plan` |
| Function app | `<BASE_NAME>-func` |
| Application Insights + Log Analytics | `<BASE_NAME>-ai` / `<BASE_NAME>-log` |

## Local development

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local), [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite).

```powershell
cd BcNuGetHelper
func start
```

Without a `PackagesStorageAccountName` setting the app falls back to the local Azurite emulator (`UseDevelopmentStorage=true`).

## Repository layout

```
.github/workflows/deploy.yml            Full deployment (Terraform + function app)
.github/workflows/deploy-function.yml   Function app only (manual trigger)
terraform/                              Terraform configuration
BcNuGetHelper/                          Azure Function app (.NET 10 isolated)
```

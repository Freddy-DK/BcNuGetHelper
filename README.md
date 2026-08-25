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
| `symbols` | `https://<functionapp>.azurewebsites.net/api/symbols/index.json` | Symbols-only packages (created with altool during upload) |

Each feed implements the NuGet v3 resources needed by [BcContainerHelper](https://github.com/microsoft/navcontainerhelper)'s NuGet search functionality:

- **Service index** — `GET api/{feed}/index.json`
- **SearchQueryService** — `GET api/{feed}/query?q=<query>&skip=<n>&take=<n>`
- **PackageBaseAddress (flat container)** —
  - `GET api/{feed}/package/{id}/index.json` (version list)
  - `GET api/{feed}/package/{id}/{version}/{id}.{version}.nupkg` (download)
- **Direct .app download** — `GET api/{feed}/download/{id}/{version}` returns the raw `.app` for that flavor. `{version}` may be `latest`. These are stable, non-expiring URLs (anonymous for public feeds).
- **Logo** — `GET api/logo/{id}` (or `api/logo/{id}/{version}`) returns the app logo extracted from the `.app` on upload.

There is no NuGet push/publish support. All uploads go through the upload endpoint:

- **Upload** — `POST api/upload` (requires a Microsoft Entra bearer token via the `Authorization: Bearer` header). Accepts a raw `.app` file body or `multipart/form-data` with one or more `.app` files (Business Central apps and their dependencies).

Uploaded apps are processed with the [AL development tools](https://learn.microsoft.com/dynamics365/business-central/dev-itpro/developer/devenv-al-tool-package) (`altool`, bundled with the deployment): the manifest (id, name, publisher, version, dependencies) is extracted, a symbols-only package is created for the symbols feed, and everything is wrapped as NuGet packages with dependency information and stored under `{feed}/{packageId}/{version}/` in the `packages` blob container.

## Public and private feeds

Each feed is either **public** (anonymous read access) or **private** (requires an access key). Which feeds are public is controlled by the `PUBLIC_FEEDS` repository variable; all feeds are private by default.

Package **metadata** (service index, search, version lists, nuspec and logo) is served anonymously as soon as **at least one** feed is public \u2014 only the package/app **content** downloads (`.nupkg` files and the `api/{feed}/download/...` `.app` endpoint) remain gated per feed. If **no** feed is public, metadata also requires an access key, so a fully private deployment exposes nothing anonymously.

Access keys are managed through Entra-protected endpoints:

| Endpoint | Description |
|----------|-------------|
| `GET api/accesskeys/{name}` | Get an access key (name, key and feeds) |
| `POST api/accesskeys/{name}` | Create an access key. Body: `{ "feeds": ["apps", "runtime", "symbols"] }` — the feeds the key grants access to. Returns the generated key |
| `DELETE api/accesskeys/{name}` | Remove an access key |

```powershell
$token = az account get-access-token --resource https://management.core.windows.net/ --query accessToken -o tsv
Invoke-RestMethod `
    -Method Post `
    -Uri "https://<functionapp>.azurewebsites.net/api/accesskeys/partner1" `
    -Headers @{ Authorization = "Bearer $token" } `
    -Body '{ "feeds": ["apps", "symbols"] }' `
    -ContentType "application/json"
```

Keys are stored in a private blob (`config/accesskeys.json`) that is managed exclusively by these endpoints — do not edit it manually. The registry is loaded into memory at startup and kept in memory for the lifetime of the function app.

When accessing a private feed, clients pass the key as basic auth password, `Authorization: Bearer` header, `X-NuGet-ApiKey` header, or `?token=` query parameter.

### Using the feeds with BcContainerHelper

```powershell
$feedUrl = "https://<functionapp>.azurewebsites.net/api/apps/index.json"
Get-BcNuGetPackage -nuGetServerUrl $feedUrl -packageName "<publisher>.<appname>" -select Exact
```

Or register as a trusted feed (use the access key as token for private feeds):

```powershell
$bcContainerHelperConfig.TrustedNuGetFeeds = @(
    @{ "Url" = "https://<functionapp>.azurewebsites.net/api/apps/index.json"; "Token" = "<access key>" }
)
```

### Uploading apps

```powershell
$token = az account get-access-token --resource https://management.core.windows.net/ --query accessToken -o tsv
Invoke-RestMethod `
    -Method Post `
    -Uri "https://<functionapp>.azurewebsites.net/api/upload" `
    -Headers @{ Authorization = "Bearer $token" } `
    -InFile ".\MyApp_1.0.0.0.app" `
    -ContentType "application/octet-stream"
```

## Deployment

Deployment runs entirely from your fork using GitHub Actions and [Bicep](https://learn.microsoft.com/azure/azure-resource-manager/bicep/). The templates live in [`bicep/`](bicep). There is no Terraform state to manage: `az deployment` reconciles against the live resources in your subscription, applying only the differences on each run (ARM *incremental* mode). Removing a resource from a template does **not** delete it — delete such resources manually.

### 1. Fork this repository

### 2. Create a managed identity for GitHub OIDC

The deploy workflow authenticates with a **user-assigned managed identity** using GitHub OIDC federation — no app registration and no stored credentials. This identity (and the resource group) must exist before the workflow can run, so it is created once locally with your own credentials via [`bicep/bootstrap.bicep`](bicep/bootstrap.bicep). All roles are scoped to the deployment resource group only.

```powershell
$repo         = "<owner/repo>"                     # Owner and repo of BcNuGetHelper fork
$location     = "<azure location>"                 # e.g. westeurope
$baseName     = "<base name>"                      # e.g. nghfreddydk (3-17 lowercase letters/digits, globally unique)
$rg           = "$($baseName)-rg"                  # e.g. nghfreddydk-rg
$subscription = az account show --query id -o tsv  # subscription ID

# OIDC subjects. GitHub is rolling out a claim format that embeds account and repository IDs,
# so register both the classic and the ID-based subject.
$repoInfo         = Invoke-RestMethod "https://api.github.com/repos/$repo"
$subjectClassic   = "repo:$($repo):ref:refs/heads/main"
$subjectWithIds   = "repo:$($repoInfo.owner.login)@$($repoInfo.owner.id)/$($repoInfo.name)@$($repoInfo.id):ref:refs/heads/main"

# Verify the base name is available before creating anything
if ((az storage account check-name --name $baseName --query nameAvailable -o tsv) -ne "true") {
    throw "Storage account name '$baseName' is not available - choose another base name"
}
$body = '{"name": "' + $baseName + '-func", "type": "Microsoft.Web/sites"}'
if ((az rest --method post --url "https://management.azure.com/subscriptions/$subscription/providers/Microsoft.Web/checknameavailability?api-version=2023-12-01" --body $body --query nameAvailable -o tsv) -ne "true") {
    throw "Function app name '$($baseName)-func' is not available - choose another base name"
}

# Create the resource group, the deploy identity, its federated credentials and role assignments
$deploy = az deployment sub create `
    --location $location `
    --template-file bicep/bootstrap.bicep `
    --parameters baseName=$baseName location=$location resourceGroupName=$rg `
        githubSubjectClassic=$subjectClassic githubSubjectWithIds=$subjectWithIds `
    --query properties.outputs -o json | ConvertFrom-Json

# Configure the repository secrets and variables (requires gh auth login)
$tenantId = az account show --query tenantId -o tsv
gh secret set AZURE_CLIENT_ID --repo $repo --body $deploy.deployClientId.value
gh secret set AZURE_TENANT_ID --repo $repo --body $tenantId
gh secret set AZURE_SUBSCRIPTION_ID --repo $repo --body $subscription

gh variable set BASE_NAME --repo $repo --body $baseName
gh variable set AZURE_LOCATION --repo $repo --body $location
gh variable set RESOURCE_GROUP_NAME --repo $repo --body $rg
# Optional: feeds served without authentication
# gh variable set PUBLIC_FEEDS --repo $repo --body "apps,runtime,symbols"
# Optional: restrict admin endpoints (upload, access keys) to a single client/application id
# gh variable set ADMIN_CLIENT_ID --repo $repo --body "<client-id>"
```

Roles granted to the deploy identity: **Contributor** (create resources), **Role Based Access Control Administrator** (create the role assignment for the function's managed identity) and **Storage Blob Data Contributor** (function content deployment to the deployments container).

> Two federated credentials are registered because GitHub is rolling out a new OIDC subject claim format that embeds account and repository IDs (`repo:owner@id/repo@id:...`). Registering both the classic and the ID-based subject makes login work either way. If login still fails with `AADSTS700213`, copy the exact "subject claim" shown in the failed *Azure login* step into a federated credential.

### 3. Configure repository settings

All settings are configured as repository **secrets** and **variables** (Settings → Secrets and variables → Actions). The script in step 2 sets them all via the [GitHub CLI](https://cli.github.com/); the tables below describe them for reference.

#### Secrets

| Secret | Required | Description |
|--------|----------|-------------|
| `AZURE_CLIENT_ID` | Yes | Client id of the managed identity created above (`az identity show --name github-deploy --resource-group $rg --query clientId -o tsv`) |
| `AZURE_TENANT_ID` | Yes | Your Entra ID tenant id |
| `AZURE_SUBSCRIPTION_ID` | Yes | The Azure subscription to deploy to |

#### Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `BASE_NAME` | Yes | — | Base name for all Azure resources. Lowercase letters and digits only, 3–17 characters, globally unique (used for storage account names). Example: `mybcnuget` |
| `AZURE_LOCATION` | Yes | — | Azure region to deploy to |
| `RESOURCE_GROUP_NAME` | No | `<BASE_NAME>-rg` | Name of the resource group (must match the one created in step 2) |
| `PUBLIC_FEEDS` | No | (empty — all feeds private) | Comma-separated list of feeds served without authentication, e.g. `apps,runtime,symbols` |
| `ADMIN_CLIENT_ID` | No | (empty — any caller from your tenant) | Restrict the admin endpoints (upload, access keys) to a single client/application id. When empty, any valid Entra token from your tenant (for the ARM audience) is accepted |

### 4. Deploy

Run the **Deploy** workflow manually (Actions → Deploy → Run workflow). The workflow:

1. Logs in to Azure using OIDC (no stored credentials)
2. Deploys `bicep/main.bicep` to the resource group (`az deployment group create`, incremental) to create/update all resources
3. Builds the .NET 10 function app and deploys it

### Resources created

| Resource | Name |
|----------|------|
| Resource group | `<RESOURCE_GROUP_NAME>` |
| Storage account (packages + deployments) | `<BASE_NAME>` |
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

## Website (GitHub Pages)

A static, brandable catalog website can be published to GitHub Pages. It lists every app that is available on a public feed and, per app, shows the logo, description and dependencies (from the latest version) plus a table of all versions with direct download links for each public feed flavor (Full app / Runtime / Symbols).

Because browser downloads only work for public feeds, the site lists apps only when at least one feed is public (`PUBLIC_FEEDS`). If no feed is public, only the branded front page is rendered. The build reads metadata anonymously from the public feeds — no credentials are used.

To enable it:

1. In your fork, go to **Settings → Pages** and set **Source** to **GitHub Actions**.
2. Set the `PUBLIC_FEEDS` variable to the feeds you want to expose (e.g. `apps,runtime,symbols`).
3. **Re-brand the site** by editing [`site/branding.json`](site/branding.json) (see below) so it shows your company instead of the defaults.
4. Run the **Deploy Pages** workflow (Actions → Deploy Pages → Run workflow).

### Company branding

Edit [`site/branding.json`](site/branding.json) (company name, tagline, colors, logo, favicon, footer, links) and drop your own files in [`site/assets/`](site/assets):

- `logo.svg` — header/app logo
- `favicon.svg` — browser icon
- `custom.css` — appended after the generated theme, so any rule you add wins

Build it locally to preview:

```powershell
./site/Build-Site.ps1 -BaseUrl "https://<functionapp>.azurewebsites.net" -PublicFeeds "apps,runtime,symbols"
# open ./_site/index.html
```

## Repository layout

```
.github/workflows/deploy.yml            Full deployment (Bicep + function app)
.github/workflows/deploy-function.yml   Function app only (manual trigger)
.github/workflows/deploy-pages.yml      Build & publish the catalog website to GitHub Pages
.github/workflows/test.yml              End-to-end tests against the deployed service
bicep/                                  Bicep templates (bootstrap + main infrastructure)
BcNuGetHelper/                          Azure Function app (.NET 10 isolated)
site/                                   Static catalog website (generator + branding)
tests/                                  Test scripts
```

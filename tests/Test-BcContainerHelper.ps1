#Requires -Version 7
<#
.SYNOPSIS
Tests locating and downloading NuGet packages from the deployed feeds using BcContainerHelper.
Expects packages to be present in the feeds (run Test-NuGetFeeds.ps1 first).
#>
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [string] $AccessToken
)

$ErrorActionPreference = "Stop"
$script:assertions = 0

function Assert {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "FAILED: $Message" }
    $script:assertions++
    Write-Host "  OK: $Message"
}

$adminHeaders = @{ Authorization = "Bearer $AccessToken" }

Write-Host "Installing BcContainerHelper"
Install-Module BcContainerHelper -Force -Scope CurrentUser
Import-Module BcContainerHelper -DisableNameChecking
Assert ($null -ne (Get-Command Get-BcNuGetPackage -ErrorAction SilentlyContinue)) "BcContainerHelper is available"

# --- Access key for the private feeds ---
$keyName = "e2e-bch"
Invoke-WebRequest -Method Delete -Uri "$BaseUrl/api/accesskeys/$keyName" -Headers $adminHeaders -SkipHttpErrorCheck | Out-Null
$created = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/accesskeys/$keyName" `
    -Headers $adminHeaders -Body '{ "feeds": ["apps", "runtime", "symbols"] }' -ContentType "application/json"
$token = $created.key

foreach ($feed in @("apps", "symbols")) {
    Write-Host "Testing BcContainerHelper against feed '$feed'"
    $feedUrl = "$BaseUrl/api/$feed/index.json"

    # Pick a package known to be in the feed
    $search = Invoke-RestMethod "$BaseUrl/api/$feed/query" -Headers @{ Authorization = "Bearer $token" }
    Assert ($search.totalHits -gt 0) "feed '$feed' contains packages (run Test-NuGetFeeds.ps1 first)"
    $package = $search.data | Select-Object -First 1
    $id = $package.id
    $version = $package.version

    $exact = Get-BcNuGetPackage -nuGetServerUrl $feedUrl -nuGetToken $token -packageName $id -version $version -select Exact
    Assert (-not [string]::IsNullOrEmpty($exact)) "Get-BcNuGetPackage locates $id $version (exact)"
    Assert (Test-Path $exact) "downloaded package exists"

    $latest = Get-BcNuGetPackage -nuGetServerUrl $feedUrl -nuGetToken $token -packageName $id
    Assert (-not [string]::IsNullOrEmpty($latest)) "Get-BcNuGetPackage locates $id (latest)"
    Assert (Test-Path $latest) "downloaded package exists"

    $appFolder = Join-Path ([System.IO.Path]::GetTempPath()) "bch-$feed-$([guid]::NewGuid())"
    Download-BcNuGetPackageToFolder -nuGetServerUrl $feedUrl -nuGetToken $token -packageName $id -version $version -folder $appFolder | Out-Null
    $appFiles = @(Get-ChildItem $appFolder -Recurse -Filter *.app)
    Assert ($appFiles.Count -gt 0) "Download-BcNuGetPackageToFolder extracts .app file(s) for $id"
}

# Runtime packages are generated asynchronously by the generate-runtime workflow (only when the
# GitHub App is configured), so they may not be present. Verify them only when the feed is filled.
$runtimeSearch = Invoke-RestMethod "$BaseUrl/api/runtime/query" -Headers @{ Authorization = "Bearer $token" }
if ($runtimeSearch.totalHits -gt 0) {
    Write-Host "Testing BcContainerHelper against feed 'runtime'"
    $package = $runtimeSearch.data | Select-Object -First 1
    $exact = Get-BcNuGetPackage -nuGetServerUrl "$BaseUrl/api/runtime/index.json" -nuGetToken $token -packageName $package.id -version $package.version -select Exact
    Assert (-not [string]::IsNullOrEmpty($exact)) "Get-BcNuGetPackage locates runtime package $($package.id) $($package.version)"
    Assert (Test-Path $exact) "downloaded runtime package exists"
}
else {
    Write-Host "Feed 'runtime' has no packages yet (generated asynchronously by the workflow) - skipping"
}

Invoke-WebRequest -Method Delete -Uri "$BaseUrl/api/accesskeys/$keyName" -Headers $adminHeaders -SkipHttpErrorCheck | Out-Null

Write-Host ""
Write-Host "All BcContainerHelper tests passed ($script:assertions assertions)"

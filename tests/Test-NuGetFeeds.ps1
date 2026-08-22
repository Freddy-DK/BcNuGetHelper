#Requires -Version 7
<#
.SYNOPSIS
End-to-end tests for the deployed NuGet feed service.
Downloads apps from the last two releases of a GitHub repository, uploads them,
and verifies the NuGet v3 endpoints on all three feeds.
Uploaded packages are intentionally left in the storage account.
#>
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [string] $AccessToken,
    [string] $AppsRepo = "Freddy-DK/MultiProjectRepo",
    [string] $GitHubToken
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$script:assertions = 0

function Assert {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "FAILED: $Message" }
    $script:assertions++
    Write-Host "  OK: $Message"
}

$adminHeaders = @{ Authorization = "Bearer $AccessToken" }

# --- Get .app files from the last two releases ---
Write-Host "Downloading apps from the last two releases of $AppsRepo"
$githubHeaders = @{ "X-GitHub-Api-Version" = "2022-11-28" }
if ($GitHubToken) { $githubHeaders.Authorization = "Bearer $GitHubToken"; Write-Host "  using GitHub token (length $($GitHubToken.Length))" }
else { Write-Host "  WARNING: no GitHub token supplied; calling the API anonymously" }
$releasesResponse = Invoke-WebRequest "https://api.github.com/repos/$AppsRepo/releases" -Headers $githubHeaders -SkipHttpErrorCheck
Write-Host "  GET /releases -> HTTP $($releasesResponse.StatusCode); rate-limit-remaining=$($releasesResponse.Headers['X-RateLimit-Remaining'])"
Assert ($releasesResponse.StatusCode -eq 200) "GitHub releases API returned 200 (got $($releasesResponse.StatusCode)): $($releasesResponse.Content)"
$allReleases = @($releasesResponse.Content | ConvertFrom-Json)
Write-Host "  releases returned (incl. drafts/prereleases): $($allReleases.Count)"
$allReleases | ForEach-Object { Write-Host "    - $($_.tag_name) draft=$($_.draft) prerelease=$($_.prerelease) assets=$($_.assets.Count)" }
$releases = @($allReleases | Where-Object { -not $_.draft } | Select-Object -First 2)
Assert ($releases.Count -ge 1) "found releases in $AppsRepo (got $($releases.Count))"

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "bcnuget-tests-$([guid]::NewGuid())"
New-Item $workDir -ItemType Directory | Out-Null
$appFiles = @()
foreach ($release in $releases) {
    Write-Host "Release: $($release.tag_name)"
    $releaseDir = Join-Path $workDir ($release.tag_name -replace '[^\w\.-]', '_')
    New-Item $releaseDir -ItemType Directory | Out-Null
    foreach ($asset in $release.assets) {
        $file = Join-Path $releaseDir $asset.name
        Invoke-WebRequest $asset.browser_download_url -Headers $githubHeaders -OutFile $file
        if ($asset.name -like "*.app") {
            $appFiles += Get-Item $file
        }
        elseif ($asset.name -like "*.zip") {
            Expand-Archive $file -DestinationPath "$file-extracted"
            $appFiles += Get-ChildItem "$file-extracted" -Recurse -Filter *.app
        }
    }
}
Assert ($appFiles.Count -gt 0) "found .app files in release assets (got $($appFiles.Count))"

# --- Upload ---
$uploaded = @()
foreach ($app in $appFiles) {
    Write-Host "Uploading $($app.Name) ($([math]::Round($app.Length / 1KB, 1)) KB)"
    $response = Invoke-WebRequest -Method Post -Uri "$BaseUrl/api/upload" `
        -Headers $adminHeaders -InFile $app.FullName -ContentType "application/octet-stream" -SkipHttpErrorCheck
    Write-Host "  POST /api/upload -> HTTP $($response.StatusCode)"
    Assert ($response.StatusCode -eq 200) "upload of $($app.Name) succeeded (got $($response.StatusCode): $($response.Content))"
    $uploaded += ($response.Content | ConvertFrom-Json).packages
}
$uploaded = @($uploaded | Sort-Object packageId, version -Unique)
Assert ($uploaded.Count -gt 0) "uploaded packages (got $($uploaded.Count))"
$uploaded | ForEach-Object { Write-Host "  $($_.packageId) $($_.version)" }

# --- Access key management ---
$keyName = "e2e-test"
Write-Host "Testing access key endpoints"
Invoke-WebRequest -Method Delete -Uri "$BaseUrl/api/accesskeys/$keyName" -Headers $adminHeaders -SkipHttpErrorCheck | Out-Null
$created = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/accesskeys/$keyName" `
    -Headers $adminHeaders -Body '{ "feeds": ["apps", "runtime", "symbols"] }' -ContentType "application/json"
Assert ($created.key.Length -eq 64) "created access key '$keyName'"
$fetched = Invoke-RestMethod -Uri "$BaseUrl/api/accesskeys/$keyName" -Headers $adminHeaders
Assert ($fetched.key -eq $created.key) "fetched access key matches created key"
$feedHeaders = @{ Authorization = "Bearer $($created.key)" }

# --- Feed endpoints ---
foreach ($feed in @("apps", "runtime", "symbols")) {
    Write-Host "Testing feed '$feed'"
    $feedUrl = "$BaseUrl/api/$feed"

    $anonymous = Invoke-WebRequest "$feedUrl/index.json" -SkipHttpErrorCheck
    if ($anonymous.StatusCode -eq 200) {
        Write-Host "  note: feed '$feed' is public (anonymous access allowed)"
    }
    else {
        Assert ($anonymous.StatusCode -eq 401) "anonymous access to private feed rejected with 401 (got $($anonymous.StatusCode))"
        $badToken = Invoke-WebRequest "$feedUrl/index.json" -Headers @{ Authorization = "Bearer invalid" } -SkipHttpErrorCheck
        Assert ($badToken.StatusCode -eq 401) "invalid token rejected with 401 (got $($badToken.StatusCode))"
    }

    $index = Invoke-RestMethod "$feedUrl/index.json" -Headers $feedHeaders
    Assert ($index.version -eq "3.0.0") "service index version is 3.0.0"
    Assert (@($index.resources | Where-Object { $_.'@type' -like "SearchQueryService*" }).Count -gt 0) "service index advertises SearchQueryService"
    Assert (@($index.resources | Where-Object { $_.'@type' -like "PackageBaseAddress*" }).Count -gt 0) "service index advertises PackageBaseAddress"

    foreach ($package in $uploaded) {
        $id = $package.packageId
        $version = $package.version

        $search = Invoke-RestMethod "$feedUrl/query?q=$id&take=100" -Headers $feedHeaders
        $hit = $search.data | Where-Object id -eq $id
        Assert ($null -ne $hit) "search finds $id"
        Assert (@($hit.versions.version) -contains $version) "search result for $id contains version $version"

        $versionIndex = Invoke-RestMethod "$feedUrl/package/$id/index.json" -Headers $feedHeaders
        Assert (@($versionIndex.versions) -contains $version) "flat container lists $id $version"

        $nupkg = Join-Path $workDir "$feed-$id.$version.nupkg"
        Invoke-WebRequest "$feedUrl/package/$id/$version/$id.$version.nupkg" -Headers $feedHeaders -OutFile $nupkg
        $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
        try {
            Assert (@($zip.Entries | Where-Object FullName -like "*.nuspec").Count -eq 1) "$id nupkg contains a nuspec"
            Assert (@($zip.Entries | Where-Object FullName -like "*.app").Count -eq 1) "$id nupkg contains an .app file"
        }
        finally {
            $zip.Dispose()
        }
    }
}

# Uploaded packages are left in the storage account on purpose; only the test key is removed
Invoke-WebRequest -Method Delete -Uri "$BaseUrl/api/accesskeys/$keyName" -Headers $adminHeaders -SkipHttpErrorCheck | Out-Null

Write-Host ""
Write-Host "All tests passed ($script:assertions assertions)"

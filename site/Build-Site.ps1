#Requires -Version 7
<#
.SYNOPSIS
Generates the static GitHub Pages site for the BcNuGetHelper feeds.

.DESCRIPTION
Reads app metadata from the (public) apps feed and produces a branded static site:
a landing page listing all apps and, per app, a page with its logo, description,
dependencies and a table of all versions with direct download links for every
public feed (apps / runtime / symbols).

Only feeds listed in -PublicFeeds get download buttons, because browser downloads
of a private feed would require a token. If no feed is public, only the front page
is rendered and no apps are listed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [string] $OutputDir = "_site",
    [string] $BrandingPath = (Join-Path $PSScriptRoot "branding.json"),
    [string] $PublicFeeds = ""
)

$ErrorActionPreference = "Stop"

$BaseUrl = $BaseUrl.TrimEnd("/")
$assetsDir = Join-Path $PSScriptRoot "assets"

$allFeeds = @("apps", "symbols", "runtime")
$feedLabels = @{ apps = "Full app"; runtime = "Runtime"; symbols = "Symbols" }
$publicFeedList = @(
    $PublicFeeds.Split([char]',', ([StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)) |
        ForEach-Object { $_.ToLowerInvariant() } |
        Where-Object { $allFeeds -contains $_ }
)

# --- Branding ---
$defaults = [ordered]@{
    companyName  = "Your Company"
    tagline      = "Business Central apps"
    primaryColor = "#0b5cab"
    accentColor  = "#1f9d55"
    logo         = "logo.svg"
    favicon      = "logo.svg"
    footerText   = "Powered by BcNuGetHelper"
    links        = @()
}
$branding = $defaults
if (Test-Path $BrandingPath) {
    $loaded = Get-Content $BrandingPath -Raw | ConvertFrom-Json
    foreach ($key in @($branding.Keys)) {
        if ($null -ne $loaded.$key -and "$($loaded.$key)" -ne "") {
            $branding[$key] = $loaded.$key
        }
    }
}

function Encode([object] $text) {
    return [System.Net.WebUtility]::HtmlEncode([string]$text)
}

function Get-DisplayName([string] $packageId) {
    # Package ids follow publisher.name.appId; the middle segment is the app name.
    $parts = $packageId.Split(".")
    if ($parts.Count -ge 3) { return $parts[1] }
    return $packageId
}

function New-Page {
    param([string] $Title, [string] $Body, [string] $AssetPrefix)
    $links = ($branding.links | ForEach-Object {
        "<a href=`"$(Encode $_.url)`">$(Encode $_.text)</a>"
    }) -join ""
    return @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(Encode $Title)</title>
<link rel="icon" href="${AssetPrefix}assets/$(Encode $branding.favicon)">
<link rel="stylesheet" href="${AssetPrefix}assets/style.css">
</head>
<body>
<header class="site-header">
  <a class="brand" href="${AssetPrefix}index.html">
    <img class="brand-logo" src="${AssetPrefix}assets/$(Encode $branding.logo)" alt="">
    <span>
      <span class="brand-name">$(Encode $branding.companyName)</span>
      <span class="brand-tagline">$(Encode $branding.tagline)</span>
    </span>
  </a>
  <nav class="site-nav">$links</nav>
</header>
<main class="content">
$Body
</main>
<footer class="site-footer">$(Encode $branding.footerText)</footer>
</body>
</html>
"@
}

# --- Output layout ---
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
$outAssets = Join-Path $OutputDir "assets"
New-Item $outAssets -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $assetsDir "*") $outAssets -Recurse -Force -Exclude "style.css"

$theme = @"
:root {
  --brand-primary: $($branding.primaryColor);
  --brand-accent: $($branding.accentColor);
}
* { box-sizing: border-box; }
body { margin: 0; font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; color: #1e293b; background: #f8fafc; }
a { color: var(--brand-primary); text-decoration: none; }
a:hover { text-decoration: underline; }
.site-header { display: flex; align-items: center; justify-content: space-between; gap: 1rem; padding: 1rem 1.5rem; background: var(--brand-primary); color: #fff; flex-wrap: wrap; }
.brand { display: flex; align-items: center; gap: .75rem; color: #fff; }
.brand:hover { text-decoration: none; }
.brand-logo { width: 40px; height: 40px; color: #fff; }
.brand-name { display: block; font-weight: 700; font-size: 1.1rem; }
.brand-tagline { display: block; font-size: .8rem; opacity: .85; }
.site-nav a { color: #fff; margin-left: 1rem; opacity: .9; }
.content { max-width: 960px; margin: 0 auto; padding: 2rem 1.5rem; }
.app-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 1rem; }
.app-card { display: flex; gap: .9rem; padding: 1rem; background: #fff; border: 1px solid #e2e8f0; border-radius: 10px; }
.app-card:hover { border-color: var(--brand-primary); text-decoration: none; }
.app-card img { width: 48px; height: 48px; object-fit: contain; border-radius: 8px; flex: none; }
.app-card .name { font-weight: 600; color: #0f172a; }
.app-card .publisher { font-size: .82rem; color: #64748b; }
.app-card .desc { font-size: .85rem; color: #475569; margin-top: .35rem; }
.app-card .version { font-size: .78rem; color: #64748b; margin-top: .5rem; }
.app-header { display: flex; gap: 1.25rem; align-items: center; margin-bottom: 1.5rem; }
.app-header img { width: 72px; height: 72px; object-fit: contain; border-radius: 12px; }
.app-header h1 { margin: 0; font-size: 1.6rem; }
.app-header .publisher { color: #64748b; }
.deps { font-size: .9rem; color: #475569; }
.deps ul { margin: .25rem 0 0; padding-left: 1.25rem; }
table.versions { width: 100%; border-collapse: collapse; margin-top: 1rem; }
table.versions th, table.versions td { text-align: left; padding: .6rem .5rem; border-bottom: 1px solid #e2e8f0; }
table.versions th { font-size: .78rem; text-transform: uppercase; letter-spacing: .03em; color: #64748b; }
.btn { display: inline-block; padding: .3rem .7rem; margin: .15rem .25rem .15rem 0; font-size: .82rem; border-radius: 6px; background: var(--brand-accent); color: #fff; }
.btn:hover { text-decoration: none; filter: brightness(.95); }
.btn-alt { background: #64748b; }
.dl-group { display: inline-flex; align-items: center; gap: .2rem; margin: .15rem .8rem .15rem 0; }
.dl-label { font-size: .78rem; color: #64748b; margin-right: .2rem; }
.feeds { margin: 1.5rem 0; }
.feeds h2 { font-size: 1rem; margin-bottom: .4rem; }
.feeds ul { list-style: none; padding: 0; margin: 0; }
.feeds li { padding: .2rem 0; }
.feeds code { background: #eef2f7; padding: .15rem .4rem; border-radius: 4px; font-size: .85rem; }
.empty { text-align: center; color: #64748b; padding: 3rem 1rem; }
.back { display: inline-block; margin-bottom: 1rem; font-size: .9rem; }
"@
$customCss = Join-Path $assetsDir "custom.css"
if (Test-Path $customCss) { $theme += "`n/* custom.css */`n" + (Get-Content $customCss -Raw) }
Set-Content (Join-Path $outAssets "style.css") $theme -Encoding utf8

# --- No public feeds: front page only ---
if ($publicFeedList.Count -eq 0) {
    $body = @"
<section class="empty">
  <h1>$(Encode $branding.companyName)</h1>
  <p>No apps are published to a public feed yet.</p>
</section>
"@
    Set-Content (Join-Path $OutputDir "index.html") (New-Page -Title $branding.companyName -Body $body -AssetPrefix "") -Encoding utf8
    Write-Host "No public feeds configured; generated front page only."
    return
}

# --- Fetch apps from the (public) apps feed ---
Write-Host "Fetching apps from $BaseUrl/api/apps"
$search = Invoke-RestMethod "$BaseUrl/api/apps/query?take=1000"
$apps = @($search.data | Sort-Object id)
Write-Host "  found $($apps.Count) app(s); public feeds: $($publicFeedList -join ', ')"

$logosDir = Join-Path $outAssets "logos"
New-Item $logosDir -ItemType Directory -Force | Out-Null

function Save-Logo([string] $id) {
    $resp = Invoke-WebRequest "$BaseUrl/api/logo/$id" -SkipHttpErrorCheck
    if ($resp.StatusCode -ne 200) { return "assets/placeholder.svg" }
    $contentType = @($resp.Headers['Content-Type'])[0]
    $ext = switch -Regex ($contentType) {
        "svg"    { "svg"; break }
        "png"    { "png"; break }
        "jpe?g"  { "jpg"; break }
        "gif"    { "gif"; break }
        default  { "img" }
    }
    $file = "$id.$ext"
    [System.IO.File]::WriteAllBytes((Join-Path $logosDir $file), $resp.RawContentStream.ToArray())
    return "assets/logos/$file"
}

$cards = @()
foreach ($app in $apps) {
    $id = $app.id
    $latest = $app.version
    $name = Get-DisplayName $id

    $description = ""
    $publisher = ""
    $dependencies = @()
    try {
        $nuspec = [xml](Invoke-RestMethod "$BaseUrl/api/apps/package/$id/$latest/$id.nuspec")
        $meta = $nuspec.package.metadata
        $description = "$($meta.description)"
        $publisher = "$($meta.authors)"
        if ($meta.dependencies -and $meta.dependencies.dependency) {
            $dependencies = @($meta.dependencies.dependency | ForEach-Object { "$($_.id) $($_.version)" })
        }
    }
    catch {
        Write-Warning "Could not read nuspec for $id $latest : $($_.Exception.Message)"
    }

    $logoRel = Save-Logo $id
    $shortDesc = if ($description.Length -gt 120) { $description.Substring(0, 117) + "..." } else { $description }

    $cards += @"
<a class="app-card" href="apps/$(Encode $id)/index.html">
  <img src="$(Encode $logoRel)" alt="">
  <span>
    <span class="name">$(Encode $name)</span>
    <span class="publisher">$(Encode $publisher)</span>
    <span class="desc">$(Encode $shortDesc)</span>
    <span class="version">Latest: $(Encode $latest)</span>
  </span>
</a>
"@

    # --- App detail page ---
    # The apps feed returns versions ascending; newest first for display.
    $versionsDesc = @($app.versions)
    [array]::Reverse($versionsDesc)
    $rows = foreach ($v in $versionsDesc) {
        $ver = $v.version
        $groups = foreach ($feed in $allFeeds) {
            if ($publicFeedList -contains $feed) {
                $appUrl = "$BaseUrl/api/$feed/download/$(Encode $id)/$(Encode $ver)"
                $nupkgUrl = "$BaseUrl/api/$feed/package/$(Encode $id)/$(Encode $ver)/$(Encode $id).$(Encode $ver).nupkg"
                "<span class=`"dl-group`"><span class=`"dl-label`">$(Encode $feedLabels[$feed])</span><a class=`"btn`" href=`"$appUrl`">.app</a><a class=`"btn btn-alt`" href=`"$nupkgUrl`">.nupkg</a></span>"
            }
        }
        "<tr><td>$(Encode $ver)</td><td>$($groups -join '')</td></tr>"
    }
    $depsHtml = if ($dependencies.Count -gt 0) {
        "<div class=`"deps`">Dependencies:<ul>" + (($dependencies | ForEach-Object { "<li>$(Encode $_)</li>" }) -join "") + "</ul></div>"
    } else { "" }
    $feedLinks = foreach ($feed in $allFeeds) {
        if ($publicFeedList -contains $feed) {
            "<li><span class=`"dl-label`">$(Encode $feedLabels[$feed])</span> <code>$BaseUrl/api/$feed/index.json</code></li>"
        }
    }
    $feedsHtml = if ($feedLinks) {
        "<div class=`"feeds`"><h2>NuGet feeds</h2><ul>$($feedLinks -join '')</ul></div>"
    } else { "" }

    $appBody = @"
<a class="back" href="../../index.html">&larr; All apps</a>
<div class="app-header">
  <img src="../../$(Encode $logoRel)" alt="">
  <div>
    <h1>$(Encode $name)</h1>
    <div class="publisher">$(Encode $publisher)</div>
  </div>
</div>
<p>$(Encode $description)</p>
$depsHtml
$feedsHtml
<table class="versions">
  <thead><tr><th>Version</th><th>Download</th></tr></thead>
  <tbody>
$($rows -join "`n")
  </tbody>
</table>
"@
    $appDir = Join-Path $OutputDir "apps/$id"
    New-Item $appDir -ItemType Directory -Force | Out-Null
    Set-Content (Join-Path $appDir "index.html") (New-Page -Title "$name - $($branding.companyName)" -Body $appBody -AssetPrefix "../../") -Encoding utf8
}

# --- Landing page ---
$listBody = if ($cards.Count -gt 0) {
    "<h1>Apps</h1>`n<div class=`"app-grid`">`n" + ($cards -join "`n") + "`n</div>"
} else {
    "<section class=`"empty`"><h1>$(Encode $branding.companyName)</h1><p>No apps published yet.</p></section>"
}
Set-Content (Join-Path $OutputDir "index.html") (New-Page -Title $branding.companyName -Body $listBody -AssetPrefix "") -Encoding utf8

Write-Host "Site generated in '$OutputDir' ($($apps.Count) app(s))."

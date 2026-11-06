<#
.SYNOPSIS
  Merges one plugin's built manifest into the root repo.json for a release.

.PARAMETER ManifestPath
  Path to the <Plugin>.json DalamudPackager wrote next to latest.zip.
.PARAMETER DownloadUrl
  Public URL of the release zip.
.PARAMETER Testing
  Publish on the testing track only: sets DownloadLinkTesting/TestingAssemblyVersion and leaves
  the stable fields as they were (or null for a first release).
.PARAMETER Changelog
  Changelog text for this version (shown in the installer).
#>
param(
    [Parameter(Mandatory)] [string] $ManifestPath,
    [Parameter(Mandatory)] [string] $DownloadUrl,
    [switch] $Testing,
    [string] $Changelog = "",
    [string] $RepoJsonPath = (Join-Path $PSScriptRoot "..\repo.json")
)

$ErrorActionPreference = "Stop"

$manifest = Get-Content -Raw -Encoding UTF8 $ManifestPath | ConvertFrom-Json
$repoRaw = Get-Content -Raw -Encoding UTF8 $RepoJsonPath
$repo = @()
if ($repoRaw.Trim().Length -gt 2) { $repo = @($repoRaw | ConvertFrom-Json) }

$existing = $repo | Where-Object { $_.InternalName -eq $manifest.InternalName } | Select-Object -First 1
$now = [int][double]::Parse((Get-Date -UFormat %s))
$internal = $manifest.InternalName

$entry = [ordered]@{
    Author                 = $manifest.Author
    Name                   = $manifest.Name
    InternalName           = $internal
    Punchline              = $manifest.Punchline
    Description            = $manifest.Description
    Changelog              = $Changelog
    Tags                   = @($manifest.Tags)
    CategoryTags           = @()
    RepoUrl                = $manifest.RepoUrl
    IconUrl                = "https://raw.githubusercontent.com/alizea1337/DalamudPlugins/main/plugins/$internal/images/icon.png"
    ImageUrls              = @()
    ApplicableVersion      = "any"
    DalamudApiLevel        = $manifest.DalamudApiLevel
    LoadRequiredState      = 0
    LoadSync               = $false
    LoadPriority           = 0
    CanUnloadAsync         = $false
    AcceptsFeedback        = $true
    FeedbackMessage        = $null
    IsHide                 = $false
    IsTestingExclusive     = $false
    AssemblyVersion        = $null
    DownloadLinkInstall    = $null
    DownloadLinkUpdate     = $null
    TestingAssemblyVersion = $null
    TestingDalamudApiLevel = $null
    DownloadLinkTesting    = $null
    LastUpdate             = $now
}

if ($existing) {
    $entry.AssemblyVersion        = $existing.AssemblyVersion
    $entry.DownloadLinkInstall    = $existing.DownloadLinkInstall
    $entry.DownloadLinkUpdate     = $existing.DownloadLinkUpdate
    $entry.TestingAssemblyVersion = $existing.TestingAssemblyVersion
    $entry.TestingDalamudApiLevel = $existing.TestingDalamudApiLevel
    $entry.DownloadLinkTesting    = $existing.DownloadLinkTesting
}

if ($Testing) {
    $entry.TestingAssemblyVersion = $manifest.AssemblyVersion
    $entry.TestingDalamudApiLevel = $manifest.DalamudApiLevel
    $entry.DownloadLinkTesting    = $DownloadUrl
    if (-not $entry.AssemblyVersion) { $entry.IsTestingExclusive = $true }
} else {
    $entry.AssemblyVersion     = $manifest.AssemblyVersion
    $entry.DownloadLinkInstall = $DownloadUrl
    $entry.DownloadLinkUpdate  = $DownloadUrl
    $entry.IsTestingExclusive  = $false
    # A stable release at or above the testing version retires the testing link.
    if ($entry.TestingAssemblyVersion -and ([version]$entry.TestingAssemblyVersion -le [version]$manifest.AssemblyVersion)) {
        $entry.TestingAssemblyVersion = $null
        $entry.TestingDalamudApiLevel = $null
        $entry.DownloadLinkTesting    = $null
    }
}

$others = @($repo | Where-Object { $_.InternalName -ne $internal })
$merged = @($others) + @([pscustomobject]$entry) | Sort-Object InternalName
$json = ConvertTo-Json -InputObject @($merged) -Depth 6
[IO.File]::WriteAllText((Resolve-Path $RepoJsonPath), $json + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host "repo.json updated for $internal $($manifest.AssemblyVersion) (testing: $Testing)"

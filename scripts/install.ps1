<#
.SYNOPSIS
    Installs dotquay (v0.17): downloads the newest release for this machine, verifies it against the
    checksum the release publishes, and puts it in ~\.local\bin.

.DESCRIPTION
    The install location is not a detail: it is the same place 'dotquay self update' expects to be
    able to write, so anything installed this way can update itself afterwards without needing an
    elevated prompt.

    A token is needed while dotquay's release project is private. Any GitLab personal access token
    that can read that project's releases will do - the same one
    'dotquay login gitlab.kemomi.de' stores works here.

.EXAMPLE
    $env:DOTQUAY_TOKEN = "glpat-..."; .\install.ps1

.EXAMPLE
    .\install.ps1 -Version 0.17.0 -Dir C:\tools
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Dir = (Join-Path $HOME ".local\bin"),
    [string] $Token = $env:DOTQUAY_TOKEN,
    [string] $Channel = $(if ($env:DOTQUAY_UPDATE_URL) { $env:DOTQUAY_UPDATE_URL } else { "https://gitlab.kemomi.de/api/v4/projects/128" })
)

$ErrorActionPreference = "Stop"

function Fail([string] $message) {
    Write-Error "install.ps1: $message"
    exit 1
}

$rid = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    "X64" { "win-x64" }
    "Arm64" { "win-arm64" }
    default { Fail "unsupported architecture: $_" }
}

$headers = @{}
if ($Token) { $headers["PRIVATE-TOKEN"] = $Token }

$releaseUrl = if ($Version) { "$Channel/releases/v$Version" } else { "$Channel/releases?per_page=1" }

Write-Host "looking up the newest dotquay for $rid"

try {
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -TimeoutSec 60
}
catch {
    Fail "could not reach $Channel. If that project is private, this is most likely a missing -Token. ($($_.Exception.Message))"
}

# ?per_page=1 returns an array, /releases/<tag> returns an object. Normalising here keeps the rest of
# the script from having to care which one it asked for.
if ($release -is [array]) { $release = $release[0] }
if (-not $release) { Fail "that channel has published no releases." }

$manifestLink = $release.assets.links | Where-Object { $_.name -eq "manifest.json" } | Select-Object -First 1
if (-not $manifestLink) {
    Fail "release $($release.tag_name) publishes no manifest.json, so there is nothing to verify a download against. It predates v0.17 - download it by hand."
}

$manifest = Invoke-RestMethod -Uri $manifestLink.url -Headers $headers -TimeoutSec 60
$asset = $manifest.assets | Where-Object { $_.rid -eq $rid } | Select-Object -First 1

if (-not $asset) {
    Fail "release $($manifest.version) publishes no build for ${rid}: it publishes $(($manifest.assets.rid) -join ', ')."
}

if (-not $asset.sha256) {
    Fail "the manifest publishes no checksum for $rid. Refusing to install something that cannot be verified."
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("dotquay-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $downloaded = Join-Path $temp "dotquay.exe"

    Write-Host "downloading dotquay $($manifest.version)"
    Invoke-WebRequest -Uri $asset.url -Headers $headers -OutFile $downloaded -UseBasicParsing -TimeoutSec 300

    $got = (Get-FileHash -Path $downloaded -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -ne $asset.sha256.ToLowerInvariant()) {
        Fail "checksum mismatch - nothing was installed.`n  expected $($asset.sha256)`n  got      $got"
    }

    # Run it before installing it, for the same reason 'dotquay self update' does: a binary that
    # hashes correctly can still be one the trimmer broke, and the only test that catches that is
    # starting it.
    $reported = (& $downloaded --version 2>$null | Select-Object -First 1)
    if (-not $reported) {
        Fail "the downloaded binary does not start on this machine. Nothing was installed."
    }

    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Move-Item -Path $downloaded -Destination (Join-Path $Dir "dotquay.exe") -Force

    Write-Host "installed dotquay $reported to $(Join-Path $Dir 'dotquay.exe')"
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

$onPath = ($env:PATH -split ';') -contains $Dir
if (-not $onPath) {
    Write-Host "note: $Dir is not on your PATH. Add it for this user with:"
    Write-Host "  [Environment]::SetEnvironmentVariable('PATH', `"$Dir;`" + [Environment]::GetEnvironmentVariable('PATH','User'), 'User')"
}

Write-Host "from here on, 'dotquay self update' keeps it current."
if ($Token) {
    Write-Host "run 'dotquay login gitlab.kemomi.de' once so it can reach the release channel on its own."
}

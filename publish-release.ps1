param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$RepoUrl = "",

    [switch]$NoUpload
)

$ErrorActionPreference = "Stop"
$Version = $Version.Trim().TrimStart("vV")
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.0.0 (three numbers, no extra .0)"
}

$root = $PSScriptRoot
$csproj = Join-Path $root "src\RDesk\RDesk.csproj"
$outDir = Join-Path $root "publish\win-x64"
$releases = Join-Path $root "Releases"
$icon = Join-Path $root "src\RDesk\Assets\rdesk.ico"
$tag = "v$Version"

function Get-GithubRepoUrl {
    if ($RepoUrl) { return $RepoUrl.Trim().TrimEnd("/") }
    if ($env:VPK_REPO_URL) { return $env:VPK_REPO_URL.Trim().TrimEnd("/") }

    Push-Location $root
    try {
        $origin = git remote get-url origin 2>$null
    } finally {
        Pop-Location
    }
    if (-not $origin) { return "" }

    $origin = $origin.Trim()
    if ($origin -match '^git@github\.com:(.+)$') {
        $path = $Matches[1]
    } elseif ($origin -match '^https://github\.com/(.+)$') {
        $path = $Matches[1]
    } elseif ($origin -match '^ssh://git@github\.com/(.+)$') {
        $path = $Matches[1]
    } else {
        return ""
    }
    $path = $path.TrimEnd("/").TrimEnd(".git")
    return "https://github.com/$path"
}

function Get-GithubToken {
    if ($env:VPK_TOKEN) { return $env:VPK_TOKEN }
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $token = (gh auth token 2>$null)
        if ($LASTEXITCODE -eq 0 -and $token) { return $token.Trim() }
    }
    return ""
}

dotnet tool update -g vpk --version 1.2.161 | Out-Null
if ($LASTEXITCODE -ne 0) {
    dotnet tool install -g vpk --version 1.2.161
    if ($LASTEXITCODE -ne 0) { throw "Could not install vpk" }
}

$tools = Join-Path $env:USERPROFILE ".dotnet\tools"
if (Test-Path $tools) {
    $env:PATH = "$tools;$env:PATH"
}

$repo = Get-GithubRepoUrl
$token = Get-GithubToken

if (-not $NoUpload) {
    if (-not $repo) {
        throw "No GitHub repo URL. Create the repo, run 'git remote add origin https://github.com/你的帳號/RDesk.git', or pass -RepoUrl."
    }
    if (-not $token) {
        throw @"
No GitHub token. Pick one:
  1. Install GitHub CLI, then:  winget install GitHub.cli
     gh auth login
  2. Or set env GITHUB_TOKEN to a PAT with 'contents: write'
After that, run this script again. (Use -NoUpload to pack locally only.)
"@
    }

    Write-Host "Downloading previous release (needed for delta packages)..."
    vpk download github --repoUrl $repo --token $token --outputDir $releases
    if ($LASTEXITCODE -ne 0) {
        Write-Host "No previous Velopack release yet (ok for the first publish)."
    }
}

dotnet publish $csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:Version=$Version -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

vpk pack --packId RDesk --packVersion $Version --packDir $outDir --mainExe RDesk.exe --packTitle RDesk --icon $icon --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

if ($NoUpload) {
    Write-Host ""
    Write-Host "Packed only. Files are in $releases"
    return
}

Write-Host "Uploading $tag to $repo ..."
vpk upload github --repoUrl $repo --token $token --outputDir $releases --publish --releaseName "RDesk $Version" --tag $tag
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed" }

Write-Host ""
Write-Host "Published $tag. Users download RDesk-win-Setup.exe from $repo/releases"
Write-Host "Unsigned: SmartScreen may warn the first time."
Write-Host "In RDesk settings, set the GitHub URL to: $repo"

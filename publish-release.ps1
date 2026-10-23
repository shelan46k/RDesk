param(
    [Parameter(Position = 0)]
    [string]$Kind = "",

    [string]$Version = "",

    [string]$RepoUrl = "",

    [switch]$NoUpload
)

$ErrorActionPreference = "Stop"
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $OutputEncoding = [Console]::OutputEncoding
    chcp 65001 | Out-Null
} catch {
}

$root = $PSScriptRoot
$csproj = Join-Path $root "src\RDesk\RDesk.csproj"
$outDir = Join-Path $root "publish\win-x64"
$releases = Join-Path $root "Releases"
$icon = Join-Path $root "src\RDesk\Assets\rdesk.ico"

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
    if ($env:GH_TOKEN) { return $env:GH_TOKEN }
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $token = (gh auth token 2>$null)
        if ($LASTEXITCODE -eq 0 -and $token) { return $token.Trim() }
    }

    $gitExe = (Get-Command git -ErrorAction SilentlyContinue).Source
    if ($gitExe) {
        $inFile = [System.IO.Path]::GetTempFileName()
        $prevEa = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            [System.IO.File]::WriteAllText($inFile, "protocol=https`r`nhost=github.com`r`n`r`n", [System.Text.Encoding]::ASCII)
            $env:GIT_TERMINAL_PROMPT = "0"
            $stdout = & cmd.exe /c "`"$gitExe`" credential fill < `"$inFile`""
            if ($LASTEXITCODE -eq 0) {
                foreach ($line in @($stdout)) {
                    if ($line -match '^password=(.+)$') {
                        $secret = $Matches[1].Trim()
                        if ($secret) { return $secret }
                    }
                }
            }
        } finally {
            $ErrorActionPreference = $prevEa
            Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue
            Remove-Item $inFile -Force -ErrorAction SilentlyContinue
        }
    }

    return ""
}

function ConvertTo-AppVersion([string]$raw) {
    if (-not $raw) { return $null }
    $raw = $raw.Trim().TrimStart("vV")
    if ($raw -notmatch '^\d+\.\d+\.\d+$') { return $null }
    return [version]$raw
}

function Get-LatestReleasedVersion([string]$repoUrl, [string]$token) {
    $found = @()

    Push-Location $root
    try {
        $remoteTags = git ls-remote --tags origin 2>$null
    } finally {
        Pop-Location
    }
    foreach ($line in @($remoteTags)) {
        if ($line -match 'refs/tags/(v?\d+\.\d+\.\d+)$') {
            $v = ConvertTo-AppVersion $Matches[1]
            if ($v) { $found += $v }
        }
    }

    if ($repoUrl -match 'github\.com/([^/]+/[^/]+)$') {
        $api = "https://api.github.com/repos/$($Matches[1])/releases?per_page=30"
        try {
            $headers = @{ "User-Agent" = "RDesk-publish" }
            if ($token) { $headers["Authorization"] = "Bearer $token" }
            $json = Invoke-RestMethod -Uri $api -Headers $headers
            foreach ($item in @($json)) {
                $v = ConvertTo-AppVersion ([string]$item.tag_name)
                if ($v) { $found += $v }
            }
        } catch {
            Write-Host "Could not read GitHub Releases API; using git tags instead."
        }
    }

    if ($found.Count -eq 0) { return $null }
    return ($found | Sort-Object | Select-Object -Last 1)
}

function Get-NextVersion([string]$kind, [string]$repoUrl, [string]$token) {
    $latest = Get-LatestReleasedVersion $repoUrl $token
    if (-not $latest) {
        $latest = [version]"0.0.0"
        Write-Host "GitHub 上還沒有版本，從 0.0.0 起算"
    } else {
        Write-Host "GitHub 目前最新是 $latest"
    }

    switch ($kind.ToLowerInvariant()) {
        "small" { return "{0}.{1}.{2}" -f $latest.Major, $latest.Minor, ($latest.Build + 1) }
        "medium" { return "{0}.{1}.0" -f $latest.Major, ($latest.Minor + 1) }
        "large" { return "{0}.0.0" -f ($latest.Major + 1) }
        default {
            throw @"
請指定 small、medium 或 large，例如：
  powershell -ExecutionPolicy Bypass -File .\publish-release.ps1 small
"@
        }
    }
}

$repo = Get-GithubRepoUrl
$token = Get-GithubToken

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $Version = $Version.Trim().TrimStart("vV")
    Write-Host "使用指定版本 $Version"
} else {
    $Version = Get-NextVersion $Kind $repo $token
    Write-Host "這次是 $Kind → $Version"
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.0.0 (three numbers, no extra .0)"
}

$tag = "v$Version"

dotnet tool update -g vpk --version 1.2.161 | Out-Null
if ($LASTEXITCODE -ne 0) {
    dotnet tool install -g vpk --version 1.2.161
    if ($LASTEXITCODE -ne 0) { throw "Could not install vpk" }
}

$tools = Join-Path $env:USERPROFILE ".dotnet\tools"
if (Test-Path $tools) {
    $env:PATH = "$tools;$env:PATH"
}

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

vpk pack --packId RDesk --packVersion $Version --packDir $outDir --mainExe RDesk.exe --packTitle RDesk --icon $icon --runtime win-x64 --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

if ($NoUpload) {
    Write-Host ""
    Write-Host "Packed $tag only. Files are in $releases"
    return
}

Write-Host "Uploading $tag to $repo ..."
vpk upload github --repoUrl $repo --token $token --outputDir $releases --publish --releaseName "RDesk $Version" --tag $tag
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed" }

Write-Host ""
Write-Host "Published $tag. Users download RDesk-win-Setup.exe from $repo/releases"
Write-Host "Unsigned: SmartScreen may warn the first time."
Write-Host "In RDesk settings, set the GitHub URL to: $repo"

<#
.SYNOPSIS
    Builds the release zip from a fresh clone of the committed code, then checks it.

.DESCRIPTION
    The Web SDK ships every file in the project folder, so publishing from a working copy can
    leak internal material (the private-artifacts folder, token.txt). This script never
    publishes from the working copy:

      1. refuses to run with uncommitted changes, or if internal files are tracked by git
      2. clones HEAD into a temp folder and publishes from there
      3. allows only BenqBridge.exe and web assets in the output; anything else fails the build
      4. fails if any pairing token found on this machine appears in the output
      5. writes the zip and re-reads it to confirm its contents

    It does not upload anything. The command to publish the release is printed at the end.

.EXAMPLE
    .\scripts\release.ps1
#>
[CmdletBinding()]
param(
    # Defaults to <repo>\dist.
    [string] $OutDir,

    # Build from HEAD even though the working copy has uncommitted changes (they are NOT included).
    [switch] $AllowDirty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $repo 'dist' }
$zipName = 'BenqBridge-win-x64.zip'

# Folders and files that must never be tracked or shipped.
$internalPattern = '(^|[\\/])(private-artifacts|ai|dist)([\\/]|$)|(^|[\\/])token\.txt$'
$allowedAssetExtensions = '.html', '.js', '.css', '.svg', '.webmanifest', '.png', '.ico'

function Invoke-Git {
    $output = & git -C $repo @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed" }
    return $output
}

function Get-RelativePath([string] $root, [string] $path) {
    return $path.Substring($root.TrimEnd('\').Length + 1).Replace('\', '/')
}

# --- 1. guards ---------------------------------------------------------------------------

$dirty = Invoke-Git status --porcelain --untracked-files=no
if ($dirty -and -not $AllowDirty) {
    throw "Uncommitted changes. The release is built from the last commit, so commit first (or pass -AllowDirty).`n$($dirty -join "`n")"
}

$trackedInternal = @(Invoke-Git ls-files) | Where-Object { $_ -match $internalPattern }
if ($trackedInternal) {
    throw "Internal files are tracked by git. Remove them from the repository first:`n$($trackedInternal -join "`n")"
}

$commit = (Invoke-Git rev-parse --short HEAD)
$versionLine = Select-String -Path (Join-Path $repo 'Program.cs') -Pattern 'Version\s*=>\s*"([^"]+)"' | Select-Object -First 1
$version = if ($versionLine) { $versionLine.Matches[0].Groups[1].Value } else { 'unknown' }
Write-Host "Building BenQ Bridge $version from commit $commit"

# --- 2. clean clone + publish --------------------------------------------------------------

$work = Join-Path ([IO.Path]::GetTempPath()) ("benq-bridge-release-" + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $work 'src'
$publish = Join-Path $work 'publish'

try {
    New-Item -ItemType Directory -Path $work | Out-Null
    Invoke-Git clone --quiet --no-hardlinks $repo $source | Out-Null

    & dotnet publish (Join-Path $source 'BenqBridge.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:CompressionEnabled=false `
        -o $publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    # Build by-products that users do not need.
    Get-ChildItem $publish -File | Where-Object {
        $_.Name -eq 'web.config' -or $_.Name -like '*.pdb' -or $_.Name -like '*.staticwebassets.endpoints.json'
    } | Remove-Item -Force

    # --- 3. allowlist --------------------------------------------------------------------

    $files = @(Get-ChildItem $publish -Recurse -File)
    $problems = New-Object System.Collections.Generic.List[string]

    foreach ($file in $files) {
        $relative = Get-RelativePath $publish $file.FullName

        if ($relative -match $internalPattern) { $problems.Add("internal file: $relative"); continue }

        if ($relative -eq 'BenqBridge.exe') { continue }

        $isWebAsset = $relative -like 'wwwroot/*' -and ($allowedAssetExtensions -contains $file.Extension.ToLowerInvariant())
        if (-not $isWebAsset) { $problems.Add("not on the allowlist: $relative") }
    }

    if (-not ($files | Where-Object { $_.Name -eq 'BenqBridge.exe' })) { $problems.Add('BenqBridge.exe is missing') }
    if (-not (Test-Path (Join-Path $publish 'wwwroot\index.html'))) { $problems.Add('wwwroot\index.html is missing') }

    if ($problems.Count -gt 0) { throw "Release contents rejected:`n  $($problems -join "`n  ")" }

    # --- 4. secret scan ------------------------------------------------------------------

    $secrets = @()
    $secrets += Get-ChildItem $repo -Recurse -Filter token.txt -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' } |
        ForEach-Object { (Get-Content $_.FullName -Raw).Trim() }
    if ($env:BENQ_BRIDGE_TOKEN) { $secrets += $env:BENQ_BRIDGE_TOKEN.Trim() }
    $secrets = @($secrets | Where-Object { $_ -and $_.Length -ge 6 } | Select-Object -Unique)

    $latin1 = [Text.Encoding]::GetEncoding(28591)
    foreach ($file in $files) {
        $text = $latin1.GetString([IO.File]::ReadAllBytes($file.FullName))
        foreach ($secret in $secrets) {
            if ($text.Contains($secret)) {
                throw "A pairing token found on this machine appears in $(Get-RelativePath $publish $file.FullName). Release aborted."
            }
        }
    }
    Write-Host "Checked $($files.Count) files against $($secrets.Count) known token(s): clean"

    # --- 5. zip ------------------------------------------------------------------------

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $zipPath = Join-Path (Resolve-Path $OutDir).Path $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($file in $files | Sort-Object FullName) {
            # Forward slashes: Windows PowerShell's own Compress-Archive writes backslashes.
            $entryName = Get-RelativePath $publish $file.FullName
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entryName, 'Optimal') | Out-Null
        }
    }
    finally { $zip.Dispose() }

    # Re-read the finished zip and compare it with what was approved above.
    $expected = @($files | ForEach-Object { Get-RelativePath $publish $_.FullName } | Sort-Object)
    $reader = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try { $actual = @($reader.Entries | ForEach-Object { $_.FullName } | Sort-Object) }
    finally { $reader.Dispose() }

    if (Compare-Object $expected $actual) { throw 'The zip does not match the approved file list.' }

    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

    Write-Host ''
    Write-Host "Zip entries:"
    $actual | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    Write-Host "$zipPath  ($sizeMb MB)"
    Write-Host "SHA-256  $hash"
    Write-Host ''
    Write-Host 'To publish (not run for you):'
    Write-Host "  gh release create v$version `"$zipPath`" --title `"BenQ Bridge $version`" --notes-file <notes.md>"
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}

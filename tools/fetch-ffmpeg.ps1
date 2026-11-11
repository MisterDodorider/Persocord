# Makes sure src/Persocord/Assets holds the one FFmpeg Persocord is tested
# with. It ships inside the zip, so normally nothing is downloaded. Only if
# the file is missing or different is that exact version fetched, and it's
# checked against its fingerprint before use. Never "the latest": a newer
# FFmpeg once removed an option Persocord relies on and broke replays.
#
# 8.0.1 works with NVIDIA drivers from 570 onwards (newer ones included),
# as well as Intel and AMD graphics and the CPU.

$ErrorActionPreference = "Stop"

$root   = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src\Persocord\Assets"
$target = Join-Path $assets "ffmpeg.exe"
$marker = Join-Path $assets "ffmpeg-version.txt"

$pinned = "8.0.1"
$sha256 = "5af82a0d4fe2b9eae211b967332ea97edfc51c6b328ca35b827e73eac560dc0d"
$url    = "https://github.com/GyanD/codexffmpeg/releases/download/$pinned/ffmpeg-$pinned-essentials_build.zip"

function Test-Pinned([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    return ((Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $sha256)
}

if (Test-Pinned $target) {
    Set-Content -Path $marker -Value $pinned
    Write-Host "FFmpeg $pinned is already here; nothing to download." -ForegroundColor Green
    exit 0
}

if (Test-Path $target) { Write-Host "The FFmpeg here isn't version $pinned; replacing it with the tested one..." }
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# Already installed with Persocord (or left from the last build): the same
# exact file is reused, checked by its fingerprint, instead of downloaded again.
$have = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Persocord\ffmpeg.exe"),
    (Join-Path $root "dist\ffmpeg.exe")
) | Where-Object { Test-Pinned $_ } | Select-Object -First 1
if ($have) {
    Copy-Item $have $target -Force
    Set-Content -Path $marker -Value $pinned
    Write-Host "FFmpeg $pinned is already installed; using that copy (nothing downloaded)." -ForegroundColor Green
    exit 0
}

$zip  = Join-Path $env:TEMP "ffmpeg-persocord.zip"
$work = Join-Path $env:TEMP "ffmpeg-persocord"

Write-Host "Downloading FFmpeg $pinned..."
# Windows' built-in curl downloads at full speed; PowerShell's own is the fallback.
$curl = Join-Path $env:SystemRoot "System32\curl.exe"
$ok = $false
if (Test-Path $curl) {
    & $curl -L --fail --retry 3 --silent --show-error -o $zip $url
    $ok = ($LASTEXITCODE -eq 0) -and (Test-Path $zip)
}
if (-not $ok) {
    try {
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        $ok = Test-Path $zip
    } catch { $ok = $false }
}
if (-not $ok) {
    throw "FFmpeg $pinned couldn't be downloaded. Check the internet connection, or put ffmpeg.exe (version $pinned) in src\Persocord\Assets."
}

Write-Host "Extracting..."
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null
$tar = Join-Path $env:SystemRoot "System32\tar.exe"
$unpacked = $false
if (Test-Path $tar) {
    & $tar -xf $zip -C $work
    $unpacked = ($LASTEXITCODE -eq 0)
}
if (-not $unpacked) {
    $ProgressPreference = "SilentlyContinue"
    Expand-Archive -Path $zip -DestinationPath $work -Force
}

$exe = Get-ChildItem -Path $work -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
if (-not $exe) { throw "ffmpeg.exe was not in the download." }
if (-not (Test-Pinned $exe.FullName)) {
    throw "The downloaded FFmpeg doesn't match the tested $pinned build, so it wasn't used."
}

Copy-Item $exe.FullName $target -Force
Set-Content -Path $marker -Value $pinned
Remove-Item $zip -Force
Remove-Item $work -Recurse -Force
Write-Host "FFmpeg $pinned is ready." -ForegroundColor Green

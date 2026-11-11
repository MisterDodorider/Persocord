# Builds a single Persocord.exe you can copy anywhere.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\Persocord\Persocord.csproj"
$ff   = Join-Path $root "src\Persocord\Assets\ffmpeg.exe"

# Makes sure the pinned FFmpeg version is there (instant when it already is;
# an older or newer copy is replaced).
& (Join-Path $PSScriptRoot "fetch-ffmpeg.ps1")
if (-not (Test-Path $ff)) {
    Write-Host "FFmpeg couldn't be downloaded. Check the internet connection and try again." -ForegroundColor Red
    exit 1
}

# A Persocord.exe in dist that's still open (a copy started from there, or
# an antivirus scanning it) can't be overwritten, which failed the build.
# Windows does let a file in use be renamed, so it's moved aside instead;
# the moved-aside copies are deleted on a later build, once they're free.
$dist = Join-Path $root "dist"
if (Test-Path $dist) {
    Get-ChildItem $dist -Filter "*.old-*" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    foreach ($name in "Persocord.exe", "ffmpeg.exe") {
        $file = Join-Path $dist $name
        if (Test-Path $file) {
            try { Remove-Item $file -Force -ErrorAction Stop }
            catch {
                try { Rename-Item $file ("$name.old-" + [guid]::NewGuid().ToString("N").Substring(0, 8)) -ErrorAction Stop }
                catch { }
            }
        }
    }
}

Write-Host "Building..."
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:PublishReadyToRun=true `
    -o (Join-Path $root "dist")

# dotnet reports failure through its exit code, not by throwing, so
# $ErrorActionPreference does not catch it. Without this check the script
# announced success after a failed build.
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Build FAILED. Copy the red error lines above and send them over." -ForegroundColor Red
    exit 1
}

# Copy FFmpeg into dist by hand. The project file asks for this too, but
# single-file publishing does not reliably carry loose files along, and a
# missing ffmpeg.exe only shows up later as recordings that die on start.
$dist = Join-Path $root "dist"
Copy-Item $ff (Join-Path $dist "ffmpeg.exe") -Force

# The microphone's noise suppression model sits beside the app.
$model = Join-Path $root "src\Persocord\Assets\voice.rnnn"
if (Test-Path $model) { Copy-Item $model (Join-Path $dist "voice.rnnn") -Force }

# The license and third-party notices go with every copy.
foreach ($doc in "LICENSE.md", "THIRD-PARTY-NOTICES.md") {
    $src = Join-Path $root $doc
    if (Test-Path $src) { Copy-Item $src (Join-Path $dist $doc) -Force }
}

# Only the 64-bit VLC engine is used; the others are dead weight.
foreach ($arch in "win-x86", "win-arm64") {
    $extra = Join-Path $dist "libvlc\$arch"
    if (Test-Path $extra) { Remove-Item $extra -Recurse -Force }
}
if (-not (Test-Path (Join-Path $dist "ffmpeg.exe"))) {
    Write-Host ""
    Write-Host "Could not put ffmpeg.exe into the dist folder." -ForegroundColor Red
    exit 1
}

$exe = Join-Path $root "dist\Persocord.exe"
if (-not (Test-Path $exe)) {
    Write-Host ""
    Write-Host "The build finished but Persocord.exe is not in dist. Something is off." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Build done. The result is in the dist folder." -ForegroundColor Green
exit 0

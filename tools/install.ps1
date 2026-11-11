# Builds Persocord and installs it for the current user.
#
# Run it for the first install and for every update. Settings live in
# %APPDATA%\Persocord and clips in their own folder; neither is touched here.
# No administrator rights are needed: everything goes under your own profile.
#
# -Auto is how Persocord runs it for an update it downloaded itself: the new
# version opens with the clip list when it's done, and if anything goes
# wrong the current version opens again (untouched) and this window stays
# open to say why.

param([switch]$Auto)

$ErrorActionPreference = "Stop"

$root   = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA "Programs\Persocord"
$exe    = Join-Path $target "Persocord.exe"
$first  = -not (Test-Path $exe)

function Say($text, $colour = "Gray") { Write-Host $text -ForegroundColor $colour }

# Stops here. After an automatic update, the version that was there opens
# again so you're never left without Persocord.
function Fail($text) {
    Say $text Red
    if ($Auto) {
        if (Test-Path $exe) { Start-Process $exe -ArgumentList "--library", "--update-failed" }
        Say ""
        Say "Persocord is open again." Yellow
        Read-Host "Press Enter to close this window" | Out-Null
    }
    exit 1
}

# Anything unexpected (a download that broke, say) ends the same way.
trap { Fail "Something went wrong: $($_.Exception.Message)" }

if ($Auto) { $Host.UI.RawUI.WindowTitle = "Updating Persocord" ; Say "Updating Persocord. This takes a few minutes; it opens again by itself." Green }

# --- 1. close a running copy ------------------------------------------------
# Asked politely first so it saves on the way out; forced only if it ignores
# the request. Older builds without the quit signal fall straight through to
# the forced close.
$running = Get-Process -Name "Persocord" -ErrorAction SilentlyContinue
if ($running) {
    Say "Closing the running Persocord..."
    try {
        $signal = [System.Threading.EventWaitHandle]::OpenExisting("Persocord.Quit")
        [void]$signal.Set()
        $signal.Dispose()
    } catch { }

    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    Get-Process -Name "Persocord" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

# --- 1b. make sure .NET 8 is here to build with ------------------------------
# Friends shouldn't have to find and install it by hand. Windows' own package
# manager (winget) is tried first; if it isn't available, Microsoft's official
# install script puts .NET in your profile, no administrator rights needed.
# Any .NET SDK from 8 up builds Persocord. It's looked for where Windows and
# this installer put it, not only on this window's PATH, so it isn't
# installed again when it's already there.
function Has-DotNet8 {
    $dirs = @(
        (Join-Path $env:ProgramFiles "dotnet"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"),
        (Join-Path $env:USERPROFILE ".dotnet")
    )
    foreach ($d in $dirs) {
        if ((Test-Path (Join-Path $d "dotnet.exe")) -and ($env:PATH -notlike "*$d*")) { $env:PATH = "$d;$env:PATH" }
    }
    try {
        foreach ($line in (& dotnet --list-sdks 2>$null)) {
            if ($line -match "^((\d+)\.\S+)" -and [int]$Matches[2] -ge 8) { $script:DotNetFound = $Matches[1]; return $true }
        }
    } catch { }
    return $false
}

if (Has-DotNet8) {
    Say ".NET SDK $script:DotNetFound is already installed; nothing to install." Green
}
else {
    Say "Installing .NET 8 (needed once, to build Persocord)..."
    $done = $false
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        & winget install --id Microsoft.DotNet.SDK.8 -e --silent --accept-package-agreements --accept-source-agreements | Out-Null
        # winget changes PATH for new windows; pick it up here as well.
        $env:PATH = [Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH", "User")
        $done = Has-DotNet8
    }
    if (-not $done) {
        $script = Join-Path $env:TEMP "dotnet-install.ps1"
        $dir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
        $curl = Join-Path $env:SystemRoot "System32\curl.exe"
        if (Test-Path $curl) { & $curl -L --fail --silent -o $script "https://dot.net/v1/dotnet-install.ps1" }
        else { $ProgressPreference = "SilentlyContinue"; Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $script -UseBasicParsing }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel 8.0 -InstallDir $dir | Out-Null
        $env:PATH = "$dir;$env:PATH"
        $env:DOTNET_ROOT = $dir
        # Remember it for next time too.
        $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
        if ($userPath -notlike "*$dir*") { [Environment]::SetEnvironmentVariable("PATH", "$dir;$userPath", "User") }
        $done = Has-DotNet8
    }
    if (-not $done) {
        Fail "Couldn't install .NET 8 automatically. Get it from https://dotnet.microsoft.com/download/dotnet/8.0 (the SDK), then run this again."
    }
    Say ".NET 8 is ready." Green
}

# --- 2. build -----------------------------------------------------------------
Say "Building. The first build takes a few minutes; later ones are quicker."
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) {
    Say ""
    Fail "The build failed, so nothing was installed and your current copy is untouched."
}

# --- 3. copy into place -------------------------------------------------------
Say "Installing to $target"
New-Item -ItemType Directory -Force -Path $target | Out-Null

$dist = Join-Path $root "dist"
# Subfolders too: the player's VLC engine keeps its plugins in them.
$files = Get-ChildItem $dist -File -Recurse |
    Where-Object { $_.Extension -ne ".pdb" -and $_.Name -ne "PersocordSetup.exe" -and $_.Name -notlike "*.old-*" }

# Copies moved aside by an earlier update (below), now free to delete.
Get-ChildItem $target -Filter "*.old-*" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

foreach ($f in $files) {
    $dest = Join-Path $target $f.FullName.Substring($dist.Length).TrimStart('\')
    New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
    # A just-closed process can hold its exe for a moment longer.
    for ($i = 0; $i -lt 6; $i++) {
        try { Copy-Item $f.FullName $dest -Force; break }
        catch {
            if ($i -eq 5) { Fail "Could not replace $($f.Name). Is Persocord still open?" }
            # Still held (a copy that wouldn't close, or an antivirus scan):
            # a file in use can still be renamed, so the old one is moved aside.
            if ($i -eq 2) {
                try { Rename-Item $dest ("$($f.Name).old-" + [guid]::NewGuid().ToString("N").Substring(0, 8)) -ErrorAction Stop }
                catch { }
            }
            Start-Sleep -Seconds 1
        }
    }
}
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $target -Force

# Older installs carried VLC engines Persocord never used.
foreach ($arch in "win-x86", "win-arm64") {
    $extra = Join-Path $target "libvlc\$arch"
    if (Test-Path $extra) { Remove-Item $extra -Recurse -Force -ErrorAction SilentlyContinue }
}

# --- 4. shortcuts ---------------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $target
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = "Persocord screen recorder"
    $lnk.Save()
}

New-Shortcut (Join-Path ([Environment]::GetFolderPath("Programs")) "Persocord.lnk")

# Desktop shortcut only the first time, so deleting it sticks.
if ($first) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath("Desktop")) "Persocord.lnk")
}

# --- 5. Apps & features entry -----------------------------------------------------
$version = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Persocord"
New-Item -Path $key -Force | Out-Null
$sizeKb = [int]((Get-ChildItem $target -File | Measure-Object Length -Sum).Sum / 1KB)

$values = @{
    DisplayName     = "Persocord"
    DisplayVersion  = $version
    Publisher       = "Persocord"
    InstallLocation = $target
    DisplayIcon     = "$exe,0"
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$target\uninstall.ps1`""
}
foreach ($k in $values.Keys) { New-ItemProperty $key -Name $k -Value $values[$k] -PropertyType String -Force | Out-Null }
New-ItemProperty $key -Name NoModify     -Value 1       -PropertyType DWord -Force | Out-Null
New-ItemProperty $key -Name NoRepair     -Value 1       -PropertyType DWord -Force | Out-Null
New-ItemProperty $key -Name EstimatedSize -Value $sizeKb -PropertyType DWord -Force | Out-Null

# --- 6. keep start-with-Windows pointing here --------------------------------------
# If it was switched on from an older copy, it still points at that copy.
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$existing = (Get-ItemProperty $run -ErrorAction SilentlyContinue).Persocord
if ($existing) { Set-ItemProperty $run -Name Persocord -Value "`"$exe`" --tray" }

# --- 7. start it --------------------------------------------------------------------
# Opening Persocord normally jumps straight to picking an area, which would be
# a strange thing to pop up after an install. A first install shows the clip
# list; an update just returns to the tray.
# After an automatic update the clip list opens, to show it worked.
if ($Auto)       { Start-Process $exe -ArgumentList "--library", "--updated" }
elseif ($first)  { Start-Process $exe -ArgumentList "--library" }
else             { Start-Process $exe -ArgumentList "--tray" }

Say ""
Say "Persocord $version is installed and running." Green
if ($Auto) { Start-Sleep -Seconds 3; exit 0 }
if ($first) {
    Say ""
    Say "To pin it: open Start, type Persocord, right-click it, choose Pin to taskbar." Green
    Say "If you pinned the old copy from C:\Persocord\dist, unpin that one." Yellow
}
exit 0

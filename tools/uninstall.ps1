# Removes Persocord. Your clips and settings are kept.

$target = $PSScriptRoot
Set-Location $env:TEMP   # cannot delete a folder while standing in it

Get-Process -Name "Persocord" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

foreach ($lnk in @(
    (Join-Path ([Environment]::GetFolderPath("Programs")) "Persocord.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Desktop"))  "Persocord.lnk"))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
}

Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name Persocord -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Persocord" -Recurse -ErrorAction SilentlyContinue

Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show(
    "Persocord has been removed.`n`nYour clips and settings were kept. To remove those too, delete:`n$env:APPDATA\Persocord`nand your clips folder.",
    "Persocord") | Out-Null

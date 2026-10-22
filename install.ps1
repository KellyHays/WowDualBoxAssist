# Publishes the app to %LocalAppData%\Programs\WowDualBoxAssist and puts a shortcut to it on the
# desktop. Run it again after changing the code to update the installed copy (close the app first).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\WowDualBoxAssist'
$exe = Join-Path $target 'WowDualBoxAssist.exe'

if (Get-Process WowDualBoxAssist -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    Write-Error 'WoW Dual Box Assist is running from the install folder. Close it and run this again.'
}

dotnet publish "$root\src\WowDualBoxAssist" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o $target -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$desktop = [Environment]::GetFolderPath('Desktop')   # follows OneDrive desktop redirection
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $desktop 'WoW Dual Box Assist.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Swap between your two WoW windows with one key'
$shortcut.Save()

Write-Host "Installed to $target"
Write-Host "Shortcut: $(Join-Path $desktop 'WoW Dual Box Assist.lnk')"

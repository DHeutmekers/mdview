# MdView uninstaller
$dest = Join-Path $env:LOCALAPPDATA 'Programs\MdView'
$progId = 'MdView.Markdown'
Get-Process MdView -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$keys = "Software\Classes\$progId", 'Software\Classes\Applications\MdView.exe',
        'Software\Microsoft\Windows\CurrentVersion\App Paths\MdView.exe', 'Software\MdView'
foreach ($k in $keys) { Remove-Item "Registry::HKEY_CURRENT_USER\$k" -Recurse -Force -ErrorAction SilentlyContinue }
Remove-ItemProperty 'Registry::HKEY_CURRENT_USER\Software\RegisteredApplications' -Name MdView -ErrorAction SilentlyContinue
foreach ($ext in '.md', '.markdown', '.mdown', '.mkd', '.mkdn') {
    $k = "Registry::HKEY_CURRENT_USER\Software\Classes\$ext"
    Remove-ItemProperty "$k\OpenWithProgids" -Name $progId -ErrorAction SilentlyContinue
    if ((Get-ItemProperty $k -ErrorAction SilentlyContinue).'(default)' -eq $progId) { Set-Item $k -Value '' }
}
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'MdView.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'MdView') -Recurse -Force -ErrorAction SilentlyContinue
Set-Location $env:TEMP
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'MdView has been removed.' -ForegroundColor Green

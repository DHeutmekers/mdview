# MdView installer - per-user, no admin rights needed.
$ErrorActionPreference = 'Stop'
$src  = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'Programs\MdView'
$exe  = Join-Path $dest 'MdView.exe'
$progId = 'MdView.Markdown'
$exts = '.md', '.markdown', '.mdown', '.mkd', '.mkdn'

Write-Host "Installing MdView to $dest ..."
Get-Process MdView -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dest | Out-Null
if ((Resolve-Path $src).Path -ne (Resolve-Path $dest).Path) {
    Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
}
Get-ChildItem $dest -Recurse -File | Unblock-File

function Set-Key($path, $values) {
    $k = "Registry::HKEY_CURRENT_USER\$path"
    if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }
    foreach ($name in $values.Keys) {
        if ($name -eq '') { Set-Item -Path $k -Value $values[$name] }
        else { New-ItemProperty -Path $k -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null }
    }
}

# ProgID
Set-Key "Software\Classes\$progId" @{ '' = 'Markdown document' }
Set-Key "Software\Classes\$progId\DefaultIcon" @{ '' = "`"$exe`",0" }
Set-Key "Software\Classes\$progId\shell\open\command" @{ '' = "`"$exe`" `"%1`"" }

# Application registration (shows up in "Open with" and Default apps)
Set-Key 'Software\Classes\Applications\MdView.exe' @{ 'FriendlyAppName' = 'MdView' }
Set-Key 'Software\Classes\Applications\MdView.exe\shell\open\command' @{ '' = "`"$exe`" `"%1`"" }
Set-Key 'Software\Microsoft\Windows\CurrentVersion\App Paths\MdView.exe' @{ '' = $exe; 'Path' = $dest }
Set-Key 'Software\MdView\Capabilities' @{ 'ApplicationName' = 'MdView'; 'ApplicationDescription' = 'Lightweight Markdown viewer' }
Set-Key 'Software\RegisteredApplications' @{ 'MdView' = 'Software\MdView\Capabilities' }

$needsChoice = @()
foreach ($ext in $exts) {
    Set-Key "Software\Classes\Applications\MdView.exe\SupportedTypes" @{ $ext = '' }
    Set-Key "Software\Classes\$ext\OpenWithProgids" @{ $progId = '' }
    Set-Key 'Software\MdView\Capabilities\FileAssociations' @{ $ext = $progId }
    # Become the default only where the user hasn't picked an app yet.
    $uc = "Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$ext\UserChoice"
    if (Test-Path $uc) {
        if ((Get-ItemProperty $uc -ErrorAction SilentlyContinue).ProgId -ne $progId) { $needsChoice += $ext }
    } else {
        Set-Key "Software\Classes\$ext" @{ '' = $progId }
    }
}

# Start menu shortcut
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'MdView.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = $dest
$s.Description = 'Markdown viewer'; $s.Save()

# Tell Explorer the associations changed
Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);'
[Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host ''
Write-Host 'MdView is installed.' -ForegroundColor Green
if ($needsChoice.Count -gt 0) {
    Write-Host ''
    Write-Host ("Another app is currently the default for " + ($needsChoice -join ', ') + '.') -ForegroundColor Yellow
    Write-Host 'Windows only lets you change that yourself:'
    Write-Host '  right-click a .md file > Open with > Choose another app > MdView > Always'
    Write-Host '(Opening Default apps settings for MdView now as well.)'
    Start-Process 'ms-settings:defaultapps?registeredAppUser=MdView' -ErrorAction SilentlyContinue
}

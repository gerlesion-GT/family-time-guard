<#
  Family Time Guard - uninstaller. Run Uninstall.cmd as administrator.
#>
param(
    [switch]$NonInteractive,
    [switch]$RemoveData
)

$ErrorActionPreference = 'Continue'
$ServiceName = 'FamilyTimeGuard'
$InstallDir  = Join-Path $env:ProgramFiles 'FamilyTimeGuard'
$DataDir     = Join-Path $env:ProgramData 'FamilyTimeGuard'
$Exe         = Join-Path $InstallDir 'FamilyTimeGuard.exe'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Please run Uninstall.cmd and click 'Yes' when Windows asks for permission." -ForegroundColor Red
    exit 1
}

if (-not $NonInteractive) {
    $a = Read-Host "Remove Family Time Guard from this computer? [y/N]"
    if ($a -notmatch '^[Yy]') { exit 0 }
}

Write-Host "Stopping the Guard..."
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
& sc.exe delete $ServiceName | Out-Null
Get-Process -Name 'FamilyTimeGuard' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (Test-Path $Exe) { & $Exe cleanup }

Remove-Item -Recurse -Force (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Family Time Guard') -ErrorAction SilentlyContinue

# The uninstaller may be running from inside the install folder, so delete that folder last.
$keepData = $true
if ($RemoveData) { $keepData = $false }
elseif (-not $NonInteractive) {
    $a = Read-Host "Also delete settings and activity logs? [y/N]"
    if ($a -match '^[Yy]') { $keepData = $false }
}
if (-not $keepData) { Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue }

Set-Location $env:TEMP
Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue

Write-Host "Family Time Guard has been removed." -ForegroundColor Green
exit 0

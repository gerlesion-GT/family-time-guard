<#
  Family Time Guard - installer
  Run Install.cmd (it asks for administrator permission and then runs this script).
  Running it again later upgrades the app and keeps your settings.
#>
param(
    [string]$ChildName,       # "Boong" or "Chip"
    [string]$Account,         # the child's Windows user name
    [string]$Pin,             # parent PIN (4-12 digits)
    [switch]$NonInteractive   # for automated tests
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'FamilyTimeGuard'
$InstallDir  = Join-Path $env:ProgramFiles 'FamilyTimeGuard'
$DataDir     = Join-Path $env:ProgramData 'FamilyTimeGuard'
$ConfigFile  = Join-Path $DataDir 'config.json'
$Exe         = Join-Path $InstallDir 'FamilyTimeGuard.exe'
$SourceExe   = Join-Path $PSScriptRoot 'FamilyTimeGuard.exe'

function Say($text, $color = 'White') { Write-Host $text -ForegroundColor $color }
function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Read-Secret($prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
function Fail($text) { Write-Host ""; Write-Host "PROBLEM: $text" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- checks
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Please run Install.cmd and click 'Yes' when Windows asks for permission."
}
if (-not (Test-Path $SourceExe)) { Fail "FamilyTimeGuard.exe was not found next to this script. Please unzip the whole folder first." }

Say ""
Say "  Family Time Guard - Setup" 'Green'
Say "  -------------------------" 'Green'

$me = $env:USERNAME
$isUpgrade = Test-Path $ConfigFile

# ---------------------------------------------------------------- choose the child and their account
if (-not $isUpgrade -or $ChildName) {
    if (-not $ChildName) {
        Step "Whose computer is this?"
        Say "  1) Boong"
        Say "  2) Chip"
        do { $choice = Read-Host "Type 1 or 2 and press Enter" } until ($choice -in '1', '2')
        $ChildName = @('Boong', 'Chip')[[int]$choice - 1]
    }

    if (-not $Account) {
        Step "Which Windows account does $ChildName use?"
        $users = @(Get-LocalUser | Where-Object { $_.Enabled -and $_.Name -ne $me } | Sort-Object Name)
        if ($users.Count -eq 0) {
            Fail "There is no other account on this computer. First create your own administrator account, then give $ChildName a separate standard account. See INSTALL-GUIDE.txt, step 1."
        }
        for ($i = 0; $i -lt $users.Count; $i++) {
            $label = $users[$i].Name
            if ($users[$i].FullName) { $label += "  ($($users[$i].FullName))" }
            Say ("  {0}) {1}" -f ($i + 1), $label)
        }
        do { $n = Read-Host "Type the number and press Enter" } until ($n -as [int] -and [int]$n -ge 1 -and [int]$n -le $users.Count)
        $Account = $users[[int]$n - 1].Name
    }

    if ($Account -ieq $me) {
        Fail "You are logged in as '$me', which is the child's account. Log in with YOUR parent administrator account and run the installer again."
    }
}

# ---------------------------------------------------------------- make sure the child is not an administrator
if ($Account) {
    Step "Checking that '$Account' is a standard (non-administrator) account"
    $isAdmin = $false
    try {
        $isAdmin = [bool](Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop |
            Where-Object { ($_.Name -split '\\')[-1] -ieq $Account })
    } catch {
        $groupName = (Get-LocalGroup -SID 'S-1-5-32-544').Name
        $isAdmin = [bool]((net localgroup "$groupName") -match "^$([regex]::Escape($Account))$")
    }
    if ($isAdmin) {
        Say "  '$Account' is an ADMINISTRATOR. An administrator can switch Family Time Guard off." 'Yellow'
        $answer = if ($NonInteractive) { 'y' } else { Read-Host "  Change '$Account' to a standard account now? (recommended) [Y/n]" }
        if ($answer -notmatch '^[Nn]') {
            Remove-LocalGroupMember -SID 'S-1-5-32-544' -Member $Account
            $usersGroup = Get-LocalGroup -SID 'S-1-5-32-545'
            if (-not (Get-LocalGroupMember -Group $usersGroup -ErrorAction SilentlyContinue | Where-Object { ($_.Name -split '\\')[-1] -ieq $Account })) {
                Add-LocalGroupMember -Group $usersGroup -Member $Account -ErrorAction SilentlyContinue
            }
            Say "  Done. '$Account' is now a standard account (takes effect next time they sign in)." 'Green'
        } else {
            Say "  Skipped. Remember to change it later in Settings > Accounts > Other users." 'Yellow'
        }
    } else {
        Say "  OK - '$Account' is a standard account." 'Green'
    }
}

# ---------------------------------------------------------------- stop old version (upgrade)
Step "Installing program files"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $existing -and $existing.Status -ne 'Stopped'
if ($wasRunning) { Stop-Service -Name $ServiceName -Force }

# The tray helpers in the children's sessions run from the same exe and keep running when the service stops.
# Close them, then wait until Windows has really let go of the file (that can take several seconds).
function Stop-GuardProcesses {
    $procs = @(Get-Process -Name 'FamilyTimeGuard' -ErrorAction SilentlyContinue)
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    $procs | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
}
Stop-GuardProcesses

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$copied = $false
for ($try = 1; $try -le 15 -and -not $copied; $try++) {
    try {
        Copy-Item -Path $SourceExe -Destination $Exe -Force -ErrorAction Stop
        $copied = $true
    } catch {
        if ($try -eq 1) { Say "  The old program is still closing, waiting a moment..." }
        Stop-GuardProcesses
        Start-Sleep -Seconds 2
    }
}
if (-not $copied) {
    # Never leave the children unprotected: start the old version again before giving up.
    if ($wasRunning) { Start-Service -Name $ServiceName -ErrorAction SilentlyContinue }
    Fail "Could not replace $Exe because it is still in use. The previous version is running again. Restart the computer and run the installer again."
}
Unblock-File -Path $Exe -ErrorAction SilentlyContinue
foreach ($doc in 'INSTALL-GUIDE.txt', 'SETTINGS-GUIDE.txt', 'Status.cmd', 'Logs.cmd') {
    $p = Join-Path $PSScriptRoot $doc
    if (Test-Path $p) { Copy-Item $p $InstallDir -Force }
}
Copy-Item (Join-Path $PSScriptRoot 'Uninstall.ps1') $InstallDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'Uninstall.cmd') $InstallDir -Force -ErrorAction SilentlyContinue

# Settings folder: only SYSTEM and administrators may read or change it.
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
& icacls.exe $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
Say "  Program: $InstallDir"
Say "  Settings and logs: $DataDir"

# ---------------------------------------------------------------- settings
Step "Settings"
if ($isUpgrade) {
    Say "  Keeping your existing settings."
    if ($ChildName -and $Account) { & $Exe add-account --child $ChildName --account $Account }
} else {
    & $Exe init-config --child $ChildName --account $Account
    if ($LASTEXITCODE -ne 0) { Fail "Could not create the settings file." }
}

$needPin = -not ((Get-Content $ConfigFile -Raw) -match '"parentPinHash":\s*"pbkdf2')
if ($Pin -or $needPin) {
    if (-not $Pin) {
        Say "  Choose a parent PIN (4-12 digits). You need it to give extra time on the child's computer."
        Say "  Don't tell the kids!" 'Yellow'
        do {
            $p1 = Read-Secret "  New PIN"
            $p2 = Read-Secret "  Type the PIN again"
            $ok = ($p1 -eq $p2) -and ($p1 -match '^\d{4,12}$')
            if (-not $ok) { Say "  The PINs did not match or are not 4-12 digits. Try again." 'Yellow' }
        } until ($ok)
        $Pin = $p1
    }
    # Pass the PIN through an environment variable (not visible in the process list, no encoding issues).
    $env:FTG_NEW_PIN = $Pin
    try { & $Exe set-pin } finally { Remove-Item Env:\FTG_NEW_PIN -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { Fail "Could not save the PIN." }
}

# ---------------------------------------------------------------- service
Step "Starting the Guard"
$binPath = "`"$Exe`" --service"
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName 'Family Time Guard' `
        -Description 'Screen-time rules for the kids (bedtime, game time, YouTube time).' -StartupType Automatic | Out-Null
} else {
    & sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
}
# Restart automatically if it ever stops unexpectedly.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null
Start-Service -Name $ServiceName

# Start-menu shortcut for parents to edit settings (asks for administrator permission).
try {
    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Family Time Guard'
    New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path $startMenu 'Edit settings (parents).lnk'))
    $lnk.TargetPath = 'powershell.exe'
    $lnk.Arguments = "-NoProfile -WindowStyle Hidden -Command Start-Process notepad.exe -ArgumentList '$ConfigFile' -Verb RunAs"
    $lnk.IconLocation = 'notepad.exe,0'
    $lnk.Save()
    $statusCmd = Join-Path $InstallDir 'Status.cmd'
    $lnk = $shell.CreateShortcut((Join-Path $startMenu 'Check status (parents).lnk'))
    $lnk.TargetPath = 'powershell.exe'
    $lnk.Arguments = "-NoProfile -WindowStyle Hidden -Command Start-Process '$statusCmd' -Verb RunAs"
    $lnk.IconLocation = "$Exe,0"
    $lnk.Save()
    $lnk = $shell.CreateShortcut((Join-Path $startMenu 'Open activity logs (parents).lnk'))
    $lnk.TargetPath = 'powershell.exe'
    $lnk.Arguments = "-NoProfile -WindowStyle Hidden -Command Start-Process '$(Join-Path $InstallDir 'Logs.cmd')' -Verb RunAs"
    $lnk.IconLocation = 'notepad.exe,0'
    $lnk.Save()
} catch { Say "  (Could not create Start menu shortcuts: $($_.Exception.Message))" 'Yellow' }

Start-Sleep -Seconds 4
Step "Status"
& $Exe status
if ($LASTEXITCODE -ne 0) { Fail "The Guard did not start. Look at $DataDir\logs\service.log" }

Say ""
Say "  All done! Family Time Guard is now protecting this computer." 'Green'
if ($Account) { Say "  Next: sign in as '$Account' and look for the small clock icon near the time (bottom-right)." }
Say ""
exit 0

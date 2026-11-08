<#
  Family Time Guard - connect the phone remote (Telegram).
  Run "Set up phone remote (parents)" from the Start menu, or it is offered by the installer.
#>
param(
    [string]$PcName,
    [switch]$FromInstaller   # the installer starts the service itself
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'FamilyTimeGuard'
$Exe = Join-Path $env:ProgramFiles 'FamilyTimeGuard\FamilyTimeGuard.exe'

function Say($text, $color = 'White') { Write-Host $text -ForegroundColor $color }
function Read-Secret($prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Say "Please run this from the Start menu shortcut and click 'Yes' when Windows asks for permission." 'Red'
    exit 1
}
if (-not (Test-Path $Exe)) { Say "Family Time Guard is not installed. Run Install.cmd first." 'Red'; exit 1 }

Say ""
Say "  Phone remote (Telegram)" 'Green'
Say "  -----------------------" 'Green'
Say "  You will create your own private Telegram bot for THIS computer."
Say "  Each computer needs its own bot (one for Boong's PC, one for Chip's PC)." 'Yellow'
Say ""
Say "  Easiest: do this in Telegram Web on THIS computer, so you can copy and paste the key."
Say "  Open https://web.telegram.org in your browser (in YOUR parent account) and sign in, then:"
Say "   1. Search for  @BotFather  (it has a blue tick) and tap Start."
Say "   2. Send:  /newbot"
Say "   3. Give it a name, e.g.  Chip PC Guard"
Say "   4. Give it a username ending in 'bot', e.g.  chip_pc_guard_bot"
Say "      (if it is taken, add some numbers)"
Say "   5. BotFather replies with a key that looks like  1234567890:AAH...  Copy it."
Say "  Keep the key private: anyone who has it could pretend to be the bot." 'Yellow'
Say "  When you are finished, sign out of Telegram Web on this computer (Settings > Log out)." 'Yellow'
Say ""

Say "  (To skip this for now, type S and press Enter.)"
$token = ''
do {
    $token = (Read-Secret "  Paste the key here (it shows as ***) and press Enter").Trim()
    if ($token -match '^(s|skip)$') {
        Say "  Skipped. You can set it up any time from Start > Family Time Guard > Set up phone remote (parents)."
        exit 3
    }
    if ($token -notmatch '^\d{5,15}:[A-Za-z0-9_-]{30,60}$') {
        Say "  That doesn't look like a bot key (numbers, a colon, then letters). Try again, or type S to skip." 'Yellow'
        $token = ''
    }
} until ($token)

if (-not $PcName) {
    $PcName = Read-Host "  Name for this computer in messages (e.g. Chip's PC) [press Enter for automatic]"
}

$wasRunning = $false
if (-not $FromInstaller) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') { $wasRunning = $true; Stop-Service -Name $ServiceName -Force; Start-Sleep -Seconds 2 }
}

$env:FTG_TELEGRAM_TOKEN = $token
try {
    if ($PcName) { & $Exe telegram-setup --pc-name $PcName } else { & $Exe telegram-setup }
    $code = $LASTEXITCODE
} finally {
    Remove-Item Env:\FTG_TELEGRAM_TOKEN -ErrorAction SilentlyContinue
    $token = $null
    if ($wasRunning -or (-not $FromInstaller)) { Start-Service -Name $ServiceName -ErrorAction SilentlyContinue }
}

if ($code -eq 0) {
    Say ""
    Say "  Done! Try sending /status to your bot." 'Green'
} else {
    Say ""
    Say "  The phone remote was not connected. You can try again later from the Start menu:" 'Yellow'
    Say "  Family Time Guard > Set up phone remote (parents)" 'Yellow'
}
exit $code

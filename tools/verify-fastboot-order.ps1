# verify-fastboot-order.ps1 - acceptance check for the RegentFXFastBoot load-order fix.
#
# Run AFTER a game launch. Verifies two independent things:
#   1. settings.save still holds exactly one RegentFXFastBoot row, above RegentFX.
#   2. godot.log shows an EARLY activation: no LATE-ORDER, and the ARMED/INTERCEPTED/WARMED chain.
#
# Exit codes: 0 = pass, 1 = fail, 2 = nothing to check (no log / game running).
#
# The authoritative settings file is the user:// one (AppData tree), NOT the copy under the
# game folder; the engine never reads the latter. See DEVLOG 2026-09-16.

[CmdletBinding()]
param(
    [string]$UserDataRoot = (Join-Path $env:APPDATA 'SlayTheSpire2'),
    [string]$SteamId64 = '76561199466878739',
    [string]$WorkshopItemId = '3799305611'
)

$ErrorActionPreference = 'Stop'
$fail = New-Object System.Collections.Generic.List[string]
$pass = New-Object System.Collections.Generic.List[string]

function Write-Result([string]$Status, [string]$Message) {
    $color = switch ($Status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("  [{0}] {1}" -f $Status, $Message) -ForegroundColor $color
}

if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Host 'Game is running. Quit it first: the quit-time rewrite must land before checking.' -ForegroundColor Yellow
    exit 2
}

$settingsPath = Join-Path $UserDataRoot "steam\$SteamId64\settings.save"
$logPath = Join-Path $UserDataRoot 'logs\godot.log'

Write-Host "settings.save : $settingsPath"
Write-Host "godot.log     : $logPath"
Write-Host ''

# --- 1. settings.save --------------------------------------------------------
if (-not (Test-Path $settingsPath)) {
    $fail.Add("settings.save not found at $settingsPath")
} else {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $rows = @($json.mod_settings.mod_list)
    $rfx = @($rows | Where-Object { $_.id -eq 'RegentFXFastBoot' })
    $regent = @($rows | Where-Object { $_.id -eq 'RegentFX' })

    $pass.Add("settings.save: $($rows.Count) rows")

    if ($rfx.Count -ne 1) {
        $fail.Add("expected exactly 1 RegentFXFastBoot row, found $($rfx.Count). A workshop copy is still installed: unsubscribe $WorkshopItemId and re-apply the order.")
    } else {
        $pass.Add('exactly 1 RegentFXFastBoot row')
    }
    if ($regent.Count -ne 1) {
        $fail.Add("expected exactly 1 RegentFX row, found $($regent.Count)")
    }

    if ($rfx.Count -ge 1 -and $regent.Count -ge 1) {
        # The engine keys load priority by id with LAST index winning (ModManager.SortModList),
        # so the last row of each id is the effective one.
        $rfxLast = [array]::LastIndexOf([object[]]($rows.id), 'RegentFXFastBoot')
        $regentLast = [array]::LastIndexOf([object[]]($rows.id), 'RegentFX')
        if ($rfxLast -lt $regentLast) {
            $pass.Add("load priority: RegentFXFastBoot@$rfxLast < RegentFX@$regentLast (RFX loads first)")
        } else {
            $fail.Add("load priority inverted: RegentFXFastBoot@$rfxLast >= RegentFX@$regentLast -> RFX loads after RegentFX")
        }
    }
}

# --- 2. godot.log ------------------------------------------------------------
if (-not (Test-Path $logPath)) {
    $fail.Add("godot.log not found at $logPath")
} else {
    $log = Get-Content $logPath -Raw -Encoding UTF8
    $when = (Get-Item $logPath).LastWriteTime

    $late = ([regex]::Matches($log, '\[RegentFXFastBoot\] LATE-ORDER')).Count
    if ($late -gt 0) {
        $fail.Add("godot.log has $late LATE-ORDER line(s) - the launch was late-order")
    } else {
        $pass.Add('no LATE-ORDER line')
    }

    foreach ($marker in 'ARMED', 'BOUND', 'INTERCEPTED', 'WARMED') {
        $n = ([regex]::Matches($log, "\[RegentFXFastBoot\]\s+$marker")).Count
        if ($n -gt 0) { $pass.Add("$marker x$n") } else { $fail.Add("no $marker line") }
    }

    # LATE-ORDER is only meaningful for the run that just happened; flag a stale log.
    if ($when -lt (Get-Date).AddHours(-12)) {
        Write-Result 'NOTE' "godot.log was last written $when - this may not be the run you just did."
    }
}

Write-Host ''
foreach ($m in $pass) { Write-Result 'PASS' $m }
foreach ($m in $fail) { Write-Result 'FAIL' $m }
Write-Host ''

if ($fail.Count -gt 0) {
    Write-Host "RESULT: FAIL ($($fail.Count) problem(s))" -ForegroundColor Red
    exit 1
}
Write-Host 'RESULT: PASS - RegentFXFastBoot loaded before RegentFX and activated early.' -ForegroundColor Green
exit 0

# verify-fastboot-order.ps1 - acceptance check for the RegentFXFastBoot load-order fix.
#
# Run AFTER a game launch. Verifies three independent things:
#   1. settings.save holds exactly one RegentFXFastBoot row, above RegentFX.
#   2. godot.log shows an EARLY activation: no LATE-ORDER, and the ARMED/INTERCEPTED/WARMED chain.
#   3. the late-order notice behaved (RFX-3): shown at most once, with its one-shot state recorded.
#
# Exit codes: 0 = pass, 1 = fail, 2 = nothing to check (no log / game running).
#
# The authoritative settings file is the user:// one (AppData tree), NOT the copy under the
# game folder; the engine never reads the latter. See DEVLOG 2026-09-16.

[CmdletBinding()]
param(
    [string]$UserDataRoot = (Join-Path $env:APPDATA 'SlayTheSpire2'),
    [string]$SteamId64 = '76561199466878739',
    [string]$WorkshopItemId = '3799305611',
    [string]$GameModsDir = 'G:\steam\steamapps\common\Slay the Spire 2\mods'
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
        # Two rows with this id means two installs. The engine appends disabled/duplicate
        # mods at the TAIL of mod_list, and SortModList keys priority by id with LAST index
        # winning, so the tail row overrides the enabled row's position every launch.
        # Which install to keep is the user's choice: this project runs as a pure Workshop
        # subscriber (Steam Workshop $WorkshopItemId), so the expected leftover is a
        # mods_directory row from a deleted local copy, and it disappears on the next
        # launch because ModManager rebuilds mod_list from the mods actually present.
        $localDir = Join-Path $GameModsDir 'RegentFXFastBoot'
        $sources = ($rfx | ForEach-Object { $_.source }) -join ', '
        if (Test-Path $localDir) {
            $fail.Add("expected exactly 1 RegentFXFastBoot row, found $($rfx.Count) (sources: $sources). Both installs are present: delete $localDir (or unsubscribe $WorkshopItemId) so only one remains.")
        } else {
            $fail.Add("expected exactly 1 RegentFXFastBoot row, found $($rfx.Count) (sources: $sources). No local copy exists at $localDir, so this is a stale row from a deleted install: launch the game once and quit - ModManager rebuilds mod_list from the mods actually present, which drops it.")
        }
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

    # --- 3. late-order notice (RFX-3) ---------------------------------------
    # The notice is scheduled only on a DEFINITIVE late order, so it is expected to be
    # absent from a successful (early-order) run. What must hold either way:
    #   - it is shown at most once per launch;
    #   - if it was shown, its one-shot state was recorded, so it cannot reappear;
    #   - if it could not be shown, that is reported rather than silently swallowed.
    $noticeShown = ([regex]::Matches($log, 'NOTICE: late-order popup shown')).Count
    $noticeScheduled = ([regex]::Matches($log, 'NOTICE: late-order popup scheduled')).Count
    $noticeStateOk = ([regex]::Matches($log, 'NOTICE: one-shot state recorded')).Count
    $noticeStateFail = ([regex]::Matches($log, 'one-shot state could not be written')).Count
    $noticeAlready = ([regex]::Matches($log, 'already shown in an earlier launch')).Count
    # Matches the payload's actual wording. The drop lines are interpolated, so the stable
    # substring is "is dropped for this launch" (the full literal in the DLL reads
    # ".. the late-order popup is dropped for this launch"); a pattern that assumed
    # "popup is dropped for this launch" would never match.
    $noticeDropped = ([regex]::Matches($log, 'is dropped for this launch')).Count

    if ($noticeShown -gt 1) {
        $fail.Add("the late-order notice was shown $noticeShown times in one launch; it must be shown at most once")
    } elseif ($noticeShown -eq 1) {
        $pass.Add('late-order notice shown once')
        if ($noticeStateOk -ge 1) {
            $pass.Add('notice one-shot state recorded')
        } else {
            $fail.Add('the notice was shown but no one-shot state was recorded; it may appear again next launch')
        }
        if ($late -eq 0) {
            $fail.Add('the notice was shown although godot.log has no LATE-ORDER line; the notice must only fire on a definitive late order')
        }
    } else {
        # Not shown. That is correct for an early-order run, and acceptable (reported) otherwise.
        if ($noticeAlready -ge 1) {
            $pass.Add('notice correctly suppressed: already shown in an earlier launch')
        } elseif ($noticeDropped -ge 1) {
            Write-Result 'NOTE' 'the notice was scheduled but could not be shown this launch (it is offered again next launch)'
        } elseif ($noticeScheduled -ge 1) {
            Write-Result 'NOTE' 'the notice was scheduled but no outcome line was logged; check the NOTICE: lines in godot.log'
        } else {
            $pass.Add('no notice scheduled (correct for an early-order run)')
        }
        if ($noticeStateFail -ge 1) {
            $fail.Add('the notice was shown but its one-shot state file could not be written')
        }
    }

    # The notice state file is the durable half of the one-shot contract.
    $noticeFile = Join-Path $UserDataRoot 'RegentFXFastBoot\notice.json'
    if (Test-Path $noticeFile) {
        $noticeJson = Get-Content $noticeFile -Raw -Encoding UTF8
        if ($noticeJson -match '"noticeShown"\s*:\s*true') {
            $pass.Add("notice state file present and set: $noticeFile")
        } else {
            $fail.Add("notice state file exists but does not record noticeShown=true: $noticeFile")
        }
    } elseif ($noticeShown -ge 1) {
        $fail.Add("the log says the notice was shown but no state file exists at $noticeFile")
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

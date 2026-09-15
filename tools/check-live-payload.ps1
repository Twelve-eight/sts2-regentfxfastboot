# check-live-payload.ps1 - is the DLL the game will actually load the one we staged?
#
# Why this exists: the Workshop subscriber download is ASYNCHRONOUS. After a push, the
# live copy under steamapps\workshop\content can still be the previous release for a
# while. Neither refresh-workshop-payloads.ps1 (it verifies the repo's staging tree) nor
# the steamcmd push log (it only proves the upload was accepted) looks at the live copy.
# A launch in that window silently exercises the OLD DLL, and a missing notice then reads
# as a false negative - the exact failure mode that would make an acceptance run useless.
#
# Run this immediately BEFORE the acceptance launch. Exit codes:
#   0 = live copy matches the staged payload (safe to launch)
#   1 = live copy differs (Steam has not delivered it yet, or delivered something else)
#   2 = a path is missing
#
# Note: the embedded <Version>1.0.0+<sha> differs between builds of the same source at
# different commits, so a hash mismatch alone is NOT proof of stale code. This script
# therefore also compares the UTF-16 user-string sets, which are code-level and ignore
# that version blob. If the hashes differ but the string sets match, the difference is
# provenance only and the live copy is functionally current.

[CmdletBinding()]
param(
    [string]$LiveRoot  = 'G:\steam\steamapps\workshop\content\2868840\3799305611\RegentFXFastBoot',
    [string]$StagedRoot = 'G:\omp works\sts2-regentfxfastboot\workshop\content\RegentFXFastBoot'
)

$ErrorActionPreference = 'Stop'

$liveDll   = Join-Path $LiveRoot   'RegentFXFastBoot.dll'
$stagedDll = Join-Path $StagedRoot 'RegentFXFastBoot.dll'
$liveJson  = Join-Path $LiveRoot   'RegentFXFastBoot.json'

foreach ($p in @($liveDll, $stagedDll, $liveJson)) {
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p" -ForegroundColor Red; exit 2 }
}

$liveHash   = (Get-FileHash -Algorithm SHA256 $liveDll).Hash.Substring(0,16)
$stagedHash = (Get-FileHash -Algorithm SHA256 $stagedDll).Hash.Substring(0,16)
$liveVer    = (Get-Content $liveJson -Raw -Encoding UTF8 | ConvertFrom-Json).version
$stagedVer  = (Get-Content (Join-Path $StagedRoot 'RegentFXFastBoot.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version

Write-Host "live   : $liveDll"
Write-Host "         version $liveVer  sha256 $liveHash"
Write-Host "staged : $stagedDll"
Write-Host "         version $stagedVer  sha256 $stagedHash"
Write-Host ''

if ($liveHash -eq $stagedHash) {
    Write-Host "RESULT: OK - the live copy IS the staged payload (version $liveVer). Safe to launch." -ForegroundColor Green
    exit 0
}

# Hashes differ. Decide whether that is provenance only (same code) or genuinely different code.
function Get-UserStrings([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i + 1 -lt $bytes.Length; $i += 2) {
        $c = $bytes[$i]
        if ($bytes[$i+1] -eq 0 -and $c -ge 0x20 -and $c -lt 0x7f) {
            [void]$sb.Append([char]$c)
        } else {
            if ($sb.Length -ge 6) { [void]$set.Add($sb.ToString()) }
            [void]$sb.Clear()
        }
    }
    return $set
}

# Reads the AssemblyInformationalVersion string ("1.0.0+<sha>") out of the UTF-8 metadata blob.
#
# Deliberately ASCII, NOT Unicode: the UTF-16 copy is what Get-UserStrings already compares,
# so decoding UTF-16 here would compare the same bytes twice and always find them equal -
# which made the caller's "no string differs" branch unable to ever return OK. ASCII decoding
# skips the UTF-16 copy (its NUL interleaving breaks the pattern) and reads the metadata copy,
# which is exactly the copy that can differ while every user string matches.
function Get-EmbeddedVersion([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    $m = [regex]::Match($text, '\d+\.\d+\.\d+\+[0-9a-f]{40}')
    if ($m.Success) { return $m.Value }
    return ''
}

$liveStrings   = Get-UserStrings $liveDll
$stagedStrings = Get-UserStrings $stagedDll
$onlyLive   = @($liveStrings   | Where-Object { -not $stagedStrings.Contains($_) } | Sort-Object)
$onlyStaged = @($stagedStrings | Where-Object { -not $liveStrings.Contains($_) }   | Sort-Object)

Write-Host "hashes differ; comparing code-level strings (ignores the embedded version blob)"
Write-Host "  live-only strings   : $($onlyLive.Count)"
Write-Host "  staged-only strings : $($onlyStaged.Count)"
foreach ($s in $onlyLive)   { Write-Host "    live  : $s" }
foreach ($s in $onlyStaged) { Write-Host "    staged: $s" }

$allDiffs = @($onlyLive) + @($onlyStaged)
$isVersionOnly = ($allDiffs.Count -gt 0) -and
                 (@($allDiffs | Where-Object { $_ -match '^\d+\.\d+\.\d+\+[0-9a-f]{40}$' }).Count -eq $allDiffs.Count)

if ($allDiffs.Count -eq 0) {
    Write-Host "hashes differ but no UTF-16 user string differs; checking the version attribute directly"
    # The version string also exists in the UTF-8 metadata blob, which the UTF-16 scan above
    # does not read. Extract both copies and compare them explicitly, so "no string differs"
    # cannot be mistaken for "the builds are the same" when only the version changed.
    $verLive   = Get-EmbeddedVersion $liveDll
    $verStaged = Get-EmbeddedVersion $stagedDll
    Write-Host "  live   embedded version: $verLive"
    Write-Host "  staged embedded version: $verStaged"
    if ($verLive -and $verStaged -and $verLive -ne $verStaged) {
        Write-Host "RESULT: OK (provenance only) - the only difference is the embedded build sha. Safe to launch." -ForegroundColor Green
        exit 0
    }
    Write-Host "RESULT: STALE - the live copy differs from the staged payload for a reason this scan cannot attribute to the version string." -ForegroundColor Red
    exit 1
}

if ($isVersionOnly) {
    Write-Host "RESULT: OK (provenance only) - live and staged differ solely in the embedded build sha, so the live code matches. Safe to launch." -ForegroundColor Green
    exit 0
}

Write-Host "RESULT: STALE - the live copy is NOT the staged payload. The notice will not appear if you launch now." -ForegroundColor Red
Write-Host "Wait for Steam to deliver the Workshop update (or re-run the push), then run this again." -ForegroundColor Red
exit 1

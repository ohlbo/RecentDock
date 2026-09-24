<#
.SYNOPSIS
    RecentDock dual-source verification tool.

.DESCRIPTION
    Forensics + structure parsing for the two data sources, in both states of the
    Windows "Show recently opened items" toggle:
      source A: %APPDATA%\Microsoft\Windows\Recent\*.lnk
      source B: HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs

    Why this is required: every measurement in DESIGN.md section 0 came from the
    toggle being OFF, where Windows has already reclaimed both stores, so both
    sources are empty. The real shape of the data when the toggle is ON is still
    unverified: .lnk naming rules, actual MRUListEx chain, and which subkey holds
    FOLDER records.

.USAGE
    1) record baseline : .\verify-dualsource.ps1 state
    2) enable tracking : .\verify-dualsource.ps1 enable
    3) open docs + folders as instructed
    4) capture + parse : .\verify-dualsource.ps1 analyze
    5) restore         : .\verify-dualsource.ps1 disable

.NOTES
    - Run as the target user and do NOT elevate: RecentDocs lives in HKCU, and an
      elevated token can resolve to a different hive.
    - Only Start_TrackDocs (per-user pref) is touched. NoRecentDocsHistory (group
      policy) is never modified.
    - Writes no files; all output goes to the console.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('state', 'enable', 'disable', 'analyze')]
    [string]$Action = 'state'
)

$ErrorActionPreference = 'Continue'

$AdvancedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$PolicyKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'
$RecentDocs  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs'
$RecentDir   = [Environment]::GetFolderPath('Recent')

# --------------------------------------------------------------- helpers

function Get-TrackingState {
    # Group policy takes precedence over the per-user preference.
    $policy = $null
    $track = $null

    try {
        $policy = (Get-ItemProperty -Path $PolicyKey -Name NoRecentDocsHistory -ErrorAction Stop).NoRecentDocsHistory
    }
    catch {
        $policy = $null
    }

    try {
        $track = (Get-ItemProperty -Path $AdvancedKey -Name Start_TrackDocs -ErrorAction Stop).Start_TrackDocs
    }
    catch {
        $track = $null
    }

    if ($policy -eq 1) {
        $verdict = 'DISABLED (by policy)'
    }
    elseif ($null -eq $track) {
        $verdict = 'ENABLED'
    }
    elseif ($track -eq 1) {
        $verdict = 'ENABLED'
    }
    else {
        $verdict = 'DISABLED (by user)'
    }

    $trackText = '<absent>'
    if ($null -ne $track) { $trackText = [string]$track }

    $policyText = '<absent>'
    if ($null -ne $policy) { $policyText = [string]$policy }

    $result = New-Object psobject
    Add-Member -InputObject $result -MemberType NoteProperty -Name Start_TrackDocs     -Value $trackText
    Add-Member -InputObject $result -MemberType NoteProperty -Name NoRecentDocsHistory -Value $policyText
    Add-Member -InputObject $result -MemberType NoteProperty -Name Verdict             -Value $verdict
    return $result
}

function Show-TrackingState {
    param([string]$Label)

    $s = Get-TrackingState

    Write-Host ('--- tracking state (' + $Label + ') ---') -ForegroundColor Cyan
    Write-Host ('  Start_TrackDocs     : ' + $s.Start_TrackDocs)
    Write-Host ('  NoRecentDocsHistory : ' + $s.NoRecentDocsHistory)

    $color = 'Yellow'
    if ($s.Verdict -eq 'ENABLED') { $color = 'Green' }
    Write-Host ('  verdict             : ' + $s.Verdict) -ForegroundColor $color

    return $s
}

function Read-RecentLnk {
    # Source A. .lnk files carry Hidden|System, so -Force is mandatory.
    Write-Host '--- source A: Recent folder (*.lnk) ---' -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $RecentDir)) {
        Write-Host '  Recent folder missing.' -ForegroundColor Red
        return @()
    }

    $lnk = @(Get-ChildItem -LiteralPath $RecentDir -Filter '*.lnk' -Force -File -ErrorAction SilentlyContinue)

    Write-Host ('  Recent path : ' + $RecentDir)
    Write-Host ('  .lnk count  : ' + $lnk.Count)

    if ($lnk.Count -gt 0) {
        Write-Host '  --- newest 15 (LastWriteTime | name) ---'
        $newest = $lnk | Sort-Object LastWriteTime -Descending | Select-Object -First 15
        foreach ($f in $newest) {
            Write-Host ('    ' + $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + '  |  ' + $f.Name)
        }

        # Verify the .lnk naming rule and whether same-name collisions occur.
        $groups = @($lnk | Group-Object Name | Where-Object { $_.Count -gt 1 })
        Write-Host ('  duplicate-name groups : ' + $groups.Count)
    }

    # Jump-list subdirectories and desktop.ini must never be treated as recent items.
    Write-Host '  --- non-.lnk entries in Recent ---'
    $others = @(Get-ChildItem -LiteralPath $RecentDir -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ne '.lnk' })
    foreach ($o in $others) {
        Write-Host ('    ' + $o.Name + '  [' + $o.Attributes + ']')
    }

    return $lnk
}

function ConvertTo-MruIndices {
    # MRUListEx is a little-endian DWORD chain terminated by 0xFFFFFFFF.
    # NOTE: PowerShell 5.1 -shl on a [byte] operand keeps the result in Byte range
    # (0x65 -shl 8 silently becomes 0), so every operand MUST be cast to [int].
    param([byte[]]$Bytes)

    $list = New-Object System.Collections.ArrayList
    if ($null -eq $Bytes) { return , $list }
    if ($Bytes.Length -lt 4) { return , $list }

    $i = 0
    while (($i + 4) -le $Bytes.Length) {
        $v = [int]$Bytes[$i]
        $v = $v -bor ([int]$Bytes[$i + 1] -shl 8)
        $v = $v -bor ([int]$Bytes[$i + 2] -shl 16)
        $v = $v -bor ([int]$Bytes[$i + 3] -shl 24)

        if ($v -eq -1) { break }   # 0xFFFFFFFF as a signed int32

        [void]$list.Add($v)
        if ($list.Count -gt 512) { break }
        $i = $i + 4
    }

    return , $list
}

function Get-Utf16PrefixString {
    # RecentDocs record layout (confirmed by the verification run):
    #   [UTF-16LE file name][0x00 0x00][PIDL ...]
    # Returns the name plus the byte length of the name field (for PIDL slicing),
    # or $null when the prefix does not look like text.
    #
    # The name is decoded with Encoding.Unicode rather than a hand-rolled
    # lo -bor (hi -shl 8) loop: that loop is silently WRONG on PowerShell 5.1 for
    # bytes < 0x80 in the high position, and produced mojibake for CJK names.
    param([byte[]]$Bytes)

    if ($null -eq $Bytes) { return $null }
    if ($Bytes.Length -lt 4) { return $null }

    # Locate the double-NUL terminator on an even offset.
    $term = -1
    $i = 0
    while (($i + 1) -lt $Bytes.Length) {
        if (($Bytes[$i] -eq 0) -and ($Bytes[$i + 1] -eq 0)) { $term = $i; break }
        $i = $i + 2
    }

    if ($term -lt 2) { return $null }

    $name = [System.Text.Encoding]::Unicode.GetString($Bytes, 0, $term)
    if ($name.Length -lt 1) { return $null }

    # Reject anything containing control characters: that means the prefix was not
    # a text name, and the "name" field boundary we found is not trustworthy.
    foreach ($ch in $name.ToCharArray()) {
        if ([int]$ch -lt 0x20) { return $null }
    }

    $r = New-Object psobject
    Add-Member -InputObject $r -MemberType NoteProperty -Name Name       -Value $name
    Add-Member -InputObject $r -MemberType NoteProperty -Name NameBytes  -Value ($term + 2)
    Add-Member -InputObject $r -MemberType NoteProperty -Name TotalBytes -Value $Bytes.Length
    return $r
}

function Show-HexDump {
    param([byte[]]$Bytes, [int]$MaxBytes = 160, [string]$Indent = '      ')

    if ($null -eq $Bytes) { return }

    $n = $MaxBytes
    if ($Bytes.Length -lt $n) { $n = $Bytes.Length }

    $off = 0
    while ($off -lt $n) {
        $end = $off + 15
        if ($end -gt ($n - 1)) { $end = $n - 1 }

        $chunk = $Bytes[$off..$end]
        $hexParts = @()
        $ascParts = @()

        foreach ($b in $chunk) {
            $hexParts += $b.ToString('x2')
            if (($b -ge 0x20) -and ($b -lt 0x7f)) {
                $ascParts += [char]$b
            }
            else {
                $ascParts += '.'
            }
        }

        Write-Host ('{0}{1:x4}  {2,-47}  {3}' -f $Indent, $off, ($hexParts -join ' '), (-join $ascParts))
        $off = $off + 16
    }

    if ($Bytes.Length -gt $MaxBytes) {
        Write-Host ($Indent + '... (+' + ($Bytes.Length - $MaxBytes) + ' bytes)')
    }
}

function Read-RecentDocsKey {
    # Source B. Parses the MRUListEx order chain and each record's UTF-16LE name + PIDL.
    param([string]$KeyPath, [string]$Title, [int]$DumpCount = 2)

    Write-Host ('--- ' + $Title + ' ---') -ForegroundColor Cyan

    if (-not (Test-Path $KeyPath)) {
        Write-Host '  key missing.' -ForegroundColor Yellow
        return
    }

    $item = Get-Item -Path $KeyPath -ErrorAction SilentlyContinue
    Write-Host ('  values: ' + $item.ValueCount + '   subkeys: ' + $item.SubKeyCount)

    # 1) MRUListEx: the real access order as Windows sees it, more authoritative
    #    than the .lnk LastWriteTime approximation.
    $order = New-Object System.Collections.ArrayList
    $mru = $null
    try {
        $mru = (Get-ItemProperty -Path $KeyPath -Name MRUListEx -ErrorAction Stop).MRUListEx
    }
    catch {
        $mru = $null
    }

    if ($null -ne $mru) {
        $order = ConvertTo-MruIndices -Bytes $mru
        Write-Host ('  MRUListEx raw bytes : ' + $mru.Length)
        Write-Host ('  MRUListEx chain     : ' + (($order | ForEach-Object { $_ }) -join ' -> '))
        $maxIdx = '-'
        if ($order.Count -gt 0) { $maxIdx = [string](($order | Measure-Object -Maximum).Maximum) }
        Write-Host ('  => ' + $order.Count + ' entries, highest index = ' + $maxIdx)
    }
    else {
        Write-Host '  MRUListEx : <absent>' -ForegroundColor Yellow
    }

    # 2) Per-record parsing.
    $allProps = (Get-ItemProperty -Path $KeyPath -ErrorAction SilentlyContinue).PSObject.Properties
    $props = @($allProps | Where-Object { ($_.Name -notlike 'PS*') -and ($_.Name -ne 'MRUListEx') })

    $parsed = New-Object System.Collections.ArrayList
    foreach ($p in $props) {
        $bytes = $p.Value
        if ($bytes -isnot [byte[]]) { continue }

        $r = Get-Utf16PrefixString -Bytes $bytes

        $name = '<parse failed>'
        $nameBytes = 0
        $pidlBytes = $bytes.Length
        if ($null -ne $r) {
            $name = $r.Name
            $nameBytes = $r.NameBytes
            $pidlBytes = $bytes.Length - $r.NameBytes
        }

        $rec = New-Object psobject
        Add-Member -InputObject $rec -MemberType NoteProperty -Name Index     -Value ([string]$p.Name)
        Add-Member -InputObject $rec -MemberType NoteProperty -Name Name      -Value $name
        Add-Member -InputObject $rec -MemberType NoteProperty -Name NameBytes -Value $nameBytes
        Add-Member -InputObject $rec -MemberType NoteProperty -Name PidlBytes -Value $pidlBytes
        Add-Member -InputObject $rec -MemberType NoteProperty -Name Raw       -Value $bytes
        [void]$parsed.Add($rec)
    }

    # Map value index -> rank in the MRU chain.
    $rank = 0
    $byIndex = @{}
    foreach ($i in $order) {
        $rank = $rank + 1
        $byIndex[[string]$i] = $rank
    }

    # Sort by MRU rank, unranked records last.
    $sorted = @($parsed | Sort-Object -Property @{ Expression = { if ($byIndex.ContainsKey($_.Index)) { $byIndex[$_.Index] } else { 9999 } } })

    Write-Host '  --- entries (order = MRUListEx rank) ---'
    foreach ($e in $sorted) {
        $r = '-'
        if ($byIndex.ContainsKey($e.Index)) { $r = [string]$byIndex[$e.Index] }
        Write-Host ('    #' + $r + ' value[' + $e.Index + '] name=''' + $e.Name + '''  pidl=' + $e.PidlBytes + 'B')
    }

    # 3) Hex dump: the core artefact, to confirm layout and PIDL boundary by eye.
    if ($DumpCount -gt 0) {
        Write-Host '  --- hex dump (confirmed layout: UTF-16LE name, 00 00, then metadata + PIDL) ---'
        $dump = @($parsed | Select-Object -First $DumpCount)
        foreach ($e in $dump) {
            Write-Host ('    value[' + $e.Index + ']  name=''' + $e.Name + '''  total=' + $e.Raw.Length + 'B  name=' + $e.NameBytes + 'B')
            Show-HexDump -Bytes $e.Raw
        }
    }
}

function Invoke-Analyze {
    Show-TrackingState -Label 'during analysis' | Out-Null
    Write-Host ''

    $lnk = Read-RecentLnk
    Write-Host ''

    Read-RecentDocsKey -KeyPath $RecentDocs -Title 'source B: RecentDocs (root)'

    Write-Host ''
    Write-Host '--- source B: RecentDocs subkeys (extension / folder buckets) ---' -ForegroundColor Cyan
    $subs = @(Get-ChildItem -Path $RecentDocs -ErrorAction SilentlyContinue)
    Write-Host ('  subkey count: ' + $subs.Count)
    foreach ($s in $subs) {
        Write-Host ('    [' + $s.PSChildName + ']  values=' + $s.ValueCount)
    }

    # Open question from DESIGN.md: which bucket holds FOLDER records?
    $buckets = @($subs | Where-Object { $_.PSChildName -notmatch '^\.' })
    if ($buckets.Count -gt 0) {
        Write-Host ''
        Write-Host '  --- non-extension subkeys: likely folder buckets, dumping each ---' -ForegroundColor Yellow
        foreach ($s in @($buckets | Select-Object -First 5)) {
            Read-RecentDocsKey -KeyPath $s.PSPath -Title ('RecentDocs subkey ''' + $s.PSChildName + '''') -DumpCount 1
        }
    }

    Write-Host ''
    Write-Host '--- cross-check ---' -ForegroundColor Cyan
    Write-Host '  (whether RecentDocs ever holds entries the .lnk source cannot resolve)'
    Write-Host ('  .lnk files: ' + $lnk.Count)
}

# --------------------------------------------------------------- entry point

if ($Action -eq 'state') {
    Show-TrackingState -Label 'current' | Out-Null
    Write-Host ''
    Write-Host '  Baseline recorded. To run the full verification:'
    Write-Host '    .\verify-dualsource.ps1 enable'
}
elseif ($Action -eq 'enable') {
    Write-Host 'Enabling recent-documents tracking (HKCU, per-user only)...' -ForegroundColor Yellow

    $before = Get-TrackingState

    try {
        Set-ItemProperty -Path $AdvancedKey -Name Start_TrackDocs -Value 1 -Type DWord -ErrorAction Stop
    }
    catch {
        Write-Host ''
        Write-Host ('FAILED to write registry: ' + $_.Exception.Message) -ForegroundColor Red
        Write-Host 'Run this script as the target user, WITHOUT elevation.' -ForegroundColor Red
        return
    }

    Start-Sleep -Milliseconds 300
    $after = Show-TrackingState -Label 'after enable'

    if ($after.Verdict -ne 'ENABLED') {
        Write-Host ''
        Write-Host 'STILL NOT ENABLED - a group policy is overriding the per-user setting.' -ForegroundColor Red
        return
    }

    Write-Host ''
    Write-Host '  Now do this in Explorer / real apps. NOT from a PowerShell prompt:' -ForegroundColor Green
    Write-Host '  shell-based opens and the Run dialog bypass SHAddToRecentDocs and'
    Write-Host '  would falsify the test.' -ForegroundColor Green
    Write-Host ''
    Write-Host '    1. Open 3-5 real documents from Explorer (PDF / DOCX / XLSX / TXT / PNG),'
    Write-Host '       via "Open with..." or the app own File > Open dialog.'
    Write-Host '    2. Open 1-2 FOLDERS from Explorer (double-click). Folder records are the'
    Write-Host '       open question - do it at least twice, from different parents.'
    Write-Host '    3. If convenient, open two DIFFERENT files sharing the SAME name'
    Write-Host '       (e.g. two report.pdf in different folders) to probe dedup behaviour.'
    Write-Host '    4. Wait ~5 seconds so Explorer flushes its writes.'
    Write-Host ''
    Write-Host '  Then run:  .\verify-dualsource.ps1 analyze' -ForegroundColor Green
    Write-Host ''
    Write-Host '  Baseline to restore afterwards:' -ForegroundColor DarkGray
    Write-Host ('    Start_TrackDocs = ' + $before.Start_TrackDocs) -ForegroundColor DarkGray
}
elseif ($Action -eq 'disable') {
    Write-Host 'Restoring baseline: Start_TrackDocs = 0 ...' -ForegroundColor Yellow
    try {
        Set-ItemProperty -Path $AdvancedKey -Name Start_TrackDocs -Value 0 -Type DWord -ErrorAction Stop
        Show-TrackingState -Label 'after restore' | Out-Null
        Write-Host '  Restored. Windows may take a moment to re-clear Recent/RecentDocs.' -ForegroundColor Green
    }
    catch {
        Write-Host ('FAILED to write registry: ' + $_.Exception.Message) -ForegroundColor Red
        Write-Host 'Set manually: HKCU\...\Explorer\Advanced -> Start_TrackDocs (DWORD) = 0' -ForegroundColor Red
    }
}
elseif ($Action -eq 'analyze') {
    Invoke-Analyze
}

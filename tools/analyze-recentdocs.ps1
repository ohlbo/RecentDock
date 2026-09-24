<#
.SYNOPSIS
    Deep parser for the RecentDocs source (source B), with PIDL path resolution.

.DESCRIPTION
    Refines the picture from the verification run. Answers, with evidence:

    1. Full record layout: [UTF-16LE name][00 00][unicode name again][ansi name]
       [metadata: a 2-byte run count and a 2-byte string length][PIDL].
    2. The ONLY reliable way to get the true target path out of a record: hand the
       PIDL to SHGetPathFromIDListW. The leading name field is a display string and
       must not be trusted as a path source.
    3. MRUListEx ordering (authoritative access order) vs the per-extension buckets.

.NOTES
    Verified facts from the first run (Start_TrackDocs=1):
      - root key had 8 data values (0..7) plus MRUListEx, MRU chain 7 4 3 6 5 2 1 0
      - folder records live in a subkey literally named 'Folder'
      - extension buckets: .jpg .pdf .pptx .txt
      - .lnk files are named '<name>.<ext>.lnk' and live 1:1 alongside these records

    Encoding trap this script avoids: on PowerShell 5.1, `-shl` applied to a [byte]
    operand keeps the result in Byte range (0x65 -shl 8 == 0), which silently
    corrupts UTF-16LE code points whose low byte is < 0x80. Always cast to [int].

.USAGE
    .\analyze-recentdocs.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$RecentDocs = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs'

# --------------------------------------------------------------- interop

$shellApi = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ShellPidl
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    // The record's PIDL is a suffix of the value buffer and lacks its own leading
    // 2-byte cb header, so we copy the span starting at `offset` and pass it as-is:
    // the first 2 bytes of a real ITEMIDLIST happen to live at that position.
    public static string Resolve(byte[] bytes, int offset)
    {
        if (bytes == null) return null;
        if (offset <= 0 || offset >= bytes.Length) return null;

        int spanLen = bytes.Length - offset;
        if (spanLen < 3) return null;

        IntPtr buf = Marshal.AllocHGlobal(spanLen);
        try
        {
            Marshal.Copy(bytes, offset, buf, spanLen);
            StringBuilder sb = new StringBuilder(520);
            if (!SHGetPathFromIDListW(buf, sb)) return null;
            string s = sb.ToString();
            if (s == null || s.Length == 0) return null;
            return s;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
'@

$haveInterop = $true
try { Add-Type -TypeDefinition $shellApi -ErrorAction Stop }
catch { $haveInterop = $false; Write-Host ('Add-Type failed: ' + $_.Exception.Message) -ForegroundColor Yellow }

# --------------------------------------------------------------- parsers

function ConvertTo-MruIndices {
    # Little-endian DWORD chain terminated by 0xFFFFFFFF. [int] casts are mandatory.
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
        if ($v -eq -1) { break }
        [void]$list.Add($v)
        if ($list.Count -gt 512) { break }
        $i = $i + 4
    }
    return , $list
}

function Get-RecordName {
    # Layout: [UTF-16LE name][00 00][...]. Decoded via Encoding.Unicode because the
    # hand-rolled lo -bor (hi -shl 8) loop is wrong on PowerShell 5.1 for CJK names.
    param([byte[]]$Bytes)

    if ($null -eq $Bytes) { return $null }
    if ($Bytes.Length -lt 4) { return $null }

    $term = -1
    $i = 0
    while (($i + 1) -lt $Bytes.Length) {
        if (($Bytes[$i] -eq 0) -and ($Bytes[$i + 1] -eq 0)) { $term = $i; break }
        $i = $i + 2
    }
    if ($term -lt 2) { return $null }

    $name = [System.Text.Encoding]::Unicode.GetString($Bytes, 0, $term)
    if ($name.Length -lt 1) { return $null }
    foreach ($ch in $name.ToCharArray()) { if ([int]$ch -lt 0x20) { return $null } }

    $r = New-Object psobject
    Add-Member -InputObject $r -MemberType NoteProperty -Name Name      -Value $name
    Add-Member -InputObject $r -MemberType NoteProperty -Name NameBytes -Value ($term + 2)
    return $r
}

function Get-PidlOffset {
    # Locate the ITEMIDLIST inside a record.
    #
    # Confirmed layout (value[7], 152 bytes total):
    #   0x00  UTF-16LE name                       + 00 00   -> 26 bytes
    #   0x1A  metadata: run count, string length  + padding  -> 20 bytes
    #   0x26  UTF-16LE name again + '.lnk'        + 00 00
    #   0x4A  ITEMIDLIST: cb=0x0050, id bytes, 00 00         -> 76 bytes
    #   0x96  end of buffer
    #
    # Heuristics that do NOT work: "first even offset where cb is plausible and the
    # type byte is known". The name and metadata regions contain many 00 00 pairs
    # that satisfy that, so it locks on early and resolves to nothing.
    #
    # The reliable rule: an ITEMIDLIST is a chain of (2-byte cb, cb bytes) blocks
    # terminated by 00 00, and in this layout the chain always ends exactly at the
    # end of the value buffer. Score candidates by walking the chain and requiring
    # it to terminate precisely at the buffer end.
    param([byte[]]$Bytes, [int]$AfterName)

    if ($null -eq $Bytes) { return -1 }
    if ($Bytes.Length -lt 8) { return -1 }

    $start = $AfterName
    if ($start -lt 4) { $start = 4 }
    # The PIDL never starts inside the leading name field.
    $start = $start + 2

    $off = $start
    while (($off + 2) -lt $Bytes.Length) {

        $pos = $off
        $valid = $true
        $items = 0

        while ($true) {
            if (($pos + 2) -gt $Bytes.Length) { $valid = $false; break }

            $cb = [int]$Bytes[$pos] -bor ([int]$Bytes[$pos + 1] -shl 8)

            if ($cb -eq 0) {
                # Terminator: must land exactly at the end of the buffer.
                if ($pos + 2 -eq $Bytes.Length) { break }
                $valid = $false
                break
            }

            # A real item id is >= 3 bytes, even, and fully inside the buffer.
            if ($cb -lt 3) { $valid = $false; break }
            if (($cb % 2) -ne 0) { $valid = $false; break }
            if (($pos + $cb) -gt ($Bytes.Length - 2)) { $valid = $false; break }

            # First byte of item data must not be zero (zero-type items are not real).
            if ($Bytes[$pos + 2] -eq 0) { $valid = $false; break }

            $items = $items + 1
            if ($items -gt 64) { $valid = $false; break }
            $pos = $pos + $cb
        }

        if ($valid -and ($items -ge 1)) { return $off }
        $off = $off + 2
    }

    return -1
}

function Read-Records {
    param([string]$KeyPath, [string]$Title, [switch]$Quiet)

    if (-not $Quiet) { Write-Host ('=== ' + $Title + ' ===') -ForegroundColor Cyan }

    if (-not (Test-Path $KeyPath)) {
        Write-Host '  key missing.' -ForegroundColor Yellow
        return @()
    }

    $key = Get-Item -Path $KeyPath
    $names = @($key.GetValueNames())

    if (-not $Quiet) {
        Write-Host ('  value names (' + $names.Count + '): ' + ($names -join ', '))
    }

    # MRU chain: authoritative access order.
    $order = New-Object System.Collections.ArrayList
    if ($names -contains 'MRUListEx') {
        $mru = $key.GetValue('MRUListEx')
        if ($mru -is [byte[]]) { $order = ConvertTo-MruIndices -Bytes $mru }
    }

    $rankOf = @{}
    $rank = 0
    foreach ($i in $order) { $rank = $rank + 1; $rankOf[[string]$i] = $rank }

    $records = New-Object System.Collections.ArrayList
    foreach ($n in $names) {
        if ($n -eq 'MRUListEx') { continue }

        $bytes = $key.GetValue($n)
        if ($bytes -isnot [byte[]]) { continue }

        $nm = Get-RecordName -Bytes $bytes
        $displayName = '<unparsed>'
        $nameBytes = 0
        if ($null -ne $nm) { $displayName = $nm.Name; $nameBytes = $nm.NameBytes }

        $pidlOff = Get-PidlOffset -Bytes $bytes -AfterName $nameBytes
        $resolved = $null
        if ($haveInterop -and $pidlOff -gt 0) {
            $resolved = [ShellPidl]::Resolve($bytes, $pidlOff)
        }

        $rankText = '-'
        if ($rankOf.ContainsKey($n)) { $rankText = [string]$rankOf[$n] }

        $rec = New-Object psobject
        Add-Member -InputObject $rec -MemberType NoteProperty -Name Index        -Value $n
        Add-Member -InputObject $rec -MemberType NoteProperty -Name Rank         -Value $rankText
        Add-Member -InputObject $rec -MemberType NoteProperty -Name NameField    -Value $displayName
        Add-Member -InputObject $rec -MemberType NoteProperty -Name PidlOffset   -Value $pidlOff
        Add-Member -InputObject $rec -MemberType NoteProperty -Name ResolvedPath -Value $resolved
        Add-Member -InputObject $rec -MemberType NoteProperty -Name TotalBytes   -Value $bytes.Length
        Add-Member -InputObject $rec -MemberType NoteProperty -Name Raw          -Value $bytes
        [void]$records.Add($rec)
    }

    if (-not $Quiet) {
        Write-Host ''
        Write-Host '  rank | idx | name field            | pidl@  | resolved path'
        $ordered = @($records | Sort-Object -Property @{ Expression = { if ($_.Rank -eq '-') { 9999 } else { [int]$_.Rank } } })
        foreach ($r in $ordered) {
            $nm = $r.NameField
            if ($nm.Length -gt 22) { $nm = $nm.Substring(0, 19) + '...' }
            $rp = [string]$r.ResolvedPath
            if ($rp.Length -gt 58) { $rp = '...' + $rp.Substring($rp.Length - 55) }
            Write-Host ('  ' + $r.Rank.PadRight(4) + ' | ' + $r.Index.PadRight(3) + ' | ' + $nm.PadRight(22) + ' | ' + ([string]$r.PidlOffset).PadRight(6) + ' | ' + $rp)
        }
    }

    return $records
}

# --------------------------------------------------------------- run

Write-Host '### RecentDocs deep parse ###' -ForegroundColor Green
Write-Host ''
if (-not $haveInterop) {
    Write-Host 'WARNING: PIDL resolution unavailable; paths will be empty.' -ForegroundColor Yellow
    Write-Host ''
}

$root = Read-Records -KeyPath $RecentDocs -Title 'root key'

Write-Host ''
Write-Host '=== subkey buckets ===' -ForegroundColor Cyan
$subs = @(Get-ChildItem -Path $RecentDocs -ErrorAction SilentlyContinue)
foreach ($s in $subs) {
    $cnt = $s.ValueCount
    Write-Host ('  [' + $s.PSChildName + ']')
    $recs = Read-Records -KeyPath $s.PSPath -Title ('bucket ' + $s.PSChildName) -Quiet
    foreach ($r in $recs) {
        $rp = [string]$r.ResolvedPath
        if ($rp.Length -gt 70) { $rp = '...' + $rp.Substring($rp.Length - 67) }
        Write-Host ('       rank ' + $r.Rank.PadRight(3) + ' idx ' + $r.Index.PadRight(3) + ' -> ' + $rp)
    }
}

Write-Host ''
Write-Host '=== full hex dump of a CJK record (root value 7) ===' -ForegroundColor Cyan
$sample = $root | Where-Object { $_.Index -eq '7' }
if ($null -eq $sample) { $sample = $root | Select-Object -First 1 }
foreach ($s in @($sample)) {
    $b = $s.Raw
    Write-Host ('  value[' + $s.Index + '] total=' + $b.Length + 'B  nameField=''' + $s.NameField + '''  pidlOffset=' + $s.PidlOffset)
    Write-Host ('  resolved: ' + $s.ResolvedPath) -ForegroundColor Green
    $off = 0
    while ($off -lt $b.Length) {
        $end = $off + 15
        if ($end -gt ($b.Length - 1)) { $end = $b.Length - 1 }
        $chunk = $b[$off..$end]
        $hexParts = @()
        $ascParts = @()
        foreach ($x in $chunk) {
            $hexParts += $x.ToString('x2')
            if (($x -ge 0x20) -and ($x -lt 0x7f)) { $ascParts += [char]$x } else { $ascParts += '.' }
        }
        $marker = '  '
        if (($off -le $s.PidlOffset) -and ($s.PidlOffset -lt ($off + 16))) { $marker = '<=' }
        Write-Host ('  {0:x4}  {1,-47}  {2} {3}' -f $off, ($hexParts -join ' '), (-join $ascParts), $marker)
        $off = $off + 16
    }
}

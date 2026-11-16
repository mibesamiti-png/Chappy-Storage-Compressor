#requires -Version 7
# Generated-only Windows CI experiment: never select, clean, or format a host disk.
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true') {
    throw 'VHDX probe runs on disposable GitHub-hosted Windows CI only'
}
$temp = [IO.Path]::TrimEndingDirectorySeparator(
    [IO.Path]::GetFullPath($env:RUNNER_TEMP))
if (-not [IO.Directory]::Exists($temp) -or $temp -notmatch '^[A-Za-z]:\\') {
    throw 'Missing local runner TEMP directory'
}
for ($p = $temp; $p -and $p.Length -gt 3; $p = [IO.Path]::GetDirectoryName($p)) {
    if (([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'RUNNER_TEMP contains an unsafe junction'
    }
}
$nonce = [Guid]::NewGuid().ToString('N')
$sandbox = [IO.Path]::Combine($temp, 'Chappy-R217-Part29-VHDX-' + $nonce)
if ([IO.Directory]::Exists($sandbox) -or [IO.File]::Exists($sandbox)) {
    throw 'Generated VHDX sandbox collision'
}
[IO.Directory]::CreateDirectory($sandbox) | Out-Null
$marker = [IO.Path]::Combine($sandbox, '.part29-generated-only')
[IO.File]::WriteAllText($marker, 'PART29-VHDX-SYNTHETIC:' + $nonce)
$mount = [IO.Path]::Combine($sandbox, 'mounted-volume')
[IO.Directory]::CreateDirectory($mount) | Out-Null
$image = [IO.Path]::Combine($sandbox, 'synthetic.vhdx')
$createScript = [IO.Path]::Combine($sandbox, 'create-isolated.txt')
$detachScript = [IO.Path]::Combine($sandbox, 'detach-isolated.txt')
$detachSucceeded = $false
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
public static class ChappyPart29Native
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string path,
        out ulong available, out ulong total, out ulong totalFree);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPoint(string path,
        StringBuilder result, uint capacity);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(string root, StringBuilder name,
        uint nameSize, out uint serial, out uint maxComponent, out uint flags,
        StringBuilder fsName, uint fsCapacity);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint GetCompressedFileSize(string path, out uint high);
    [StructLayout(LayoutKind.Sequential)]
    public struct FileInfo
    {
        public uint Attributes;
        public FILETIME Creation;
        public FILETIME Access;
        public FILETIME Write;
        public uint Serial;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Links;
        public uint IndexHigh;
        public uint IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle file,
        out FileInfo info);
}
'@
function Assert([bool]$condition, [string]$reason) {
    if (-not $condition) { throw $reason }
}
function VolumeName([string]$path) {
    $sb = [Text.StringBuilder]::new(160)
    if (-not [ChappyPart29Native]::GetVolumeNameForVolumeMountPoint($path, $sb, 160)) {
        throw "VHDX mount volume GUID query failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    return $sb.ToString()
}
function Free([string]$path) {
    [ulong]$a = 0; [ulong]$t = 0; [ulong]$f = 0
    if (-not [ChappyPart29Native]::GetDiskFreeSpaceEx($path, [ref]$a,
        [ref]$t, [ref]$f)) { throw 'VHDX free-space query failed' }
    return [long]$a
}
function Allocated([string]$path) {
    [uint32]$hi = 0
    [uint32]$lo = [ChappyPart29Native]::GetCompressedFileSize($path, [ref]$hi)
    if ($lo -eq [uint32]::MaxValue -and
        [Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 0) {
        throw 'Allocated file bytes unavailable'
    }
    return [long](([ulong]$hi -shl 32) -bor [ulong]$lo)
}
function Identity([string]$path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $info = [ChappyPart29Native+FileInfo]::new()
        if (-not [ChappyPart29Native]::GetFileInformationByHandle($stream.SafeFileHandle,
            [ref]$info)) { throw 'NTFS file identity read failed' }
        return [pscustomobject]@{
            Volume = [ulong]$info.Serial
            Index = (([ulong]$info.IndexHigh -shl 32) -bor [ulong]$info.IndexLow)
            Links = [int]$info.Links
        }
    } finally { $stream.Dispose() }
}
function Hash([string]$path) {
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($path)))
}
function RunDiskpart([string]$script, [string]$file) {
    [IO.File]::WriteAllText($file, $script, [Text.Encoding]::ASCII)
    $result = & "$env:SystemRoot\System32\diskpart.exe" /s $file 2>&1
    $code = $LASTEXITCODE
    $message = ($result | Out-String)
    Write-Host $message
    if ($code -ne 0 -or $message -match '(Virtual Disk Service error|DiskPart has encountered an error|Access is denied|The system cannot find the file specified)') {
        throw "Generated-only diskpart failed (exit $code)"
    }
}
try {
    Assert ([IO.File]::ReadAllText($marker) -eq 'PART29-VHDX-SYNTHETIC:' + $nonce) 'Marker mismatch'
    $hostVolume = VolumeName ([IO.Path]::GetPathRoot($temp))
    $create = @(
        "create vdisk file=""$image"" maximum=512 type=expandable",
        "select vdisk file=""$image""",
        'attach vdisk',
        'convert gpt',
        'create partition primary',
        'format fs=ntfs quick label=ChappyPart29',
        "assign mount=""$mount""",
        'exit'
    ) -join ([Environment]::NewLine)
    RunDiskpart $create $createScript
    $mountRoot = $mount + [IO.Path]::DirectorySeparatorChar
    $testVolume = VolumeName $mountRoot
    Assert ($testVolume -ne $hostVolume) 'VHDX resolves to the host volume'
    $label = [Text.StringBuilder]::new(100)
    $fs = [Text.StringBuilder]::new(64)
    [uint32]$serial = 0; [uint32]$max = 0; [uint32]$flags = 0
    Assert ([ChappyPart29Native]::GetVolumeInformation($mountRoot, $label, 100,
        [ref]$serial, [ref]$max, [ref]$flags, $fs, 64)) 'VHDX FS information missing'
    Assert ($fs.ToString() -eq 'NTFS' -and $label.ToString() -eq 'ChappyPart29') 'Unexpected VHDX filesystem'
    $empty = Free $mountRoot
    Assert ($empty -gt 64MB -and $empty -lt 512MB) 'Virtual volume size is unexpected'

    $a = [IO.Path]::Combine($mount, 'generated-primary.bin')
    $b = [IO.Path]::Combine($mount, 'generated-alias.bin')
    $stage = [IO.Path]::Combine($mount, 'generated-detach-stage.bin')
    $bytes = [byte[]]::new(8MB)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [IO.File]::WriteAllBytes($a, $bytes)
    [IO.File]::Copy($a, $b)
    $hash = Hash $a
    $idA = Identity $a; $idB = Identity $b
    Assert ($idA.Index -ne $idB.Index -and $idA.Links -eq 1 -and
        $idB.Links -eq 1 -and (Hash $b) -eq $hash) 'Independent generated file proof failed'
    $allocatedBefore = (Allocated $a) + (Allocated $b)
    $freeBefore = Free $mountRoot
    [IO.File]::Delete($b)
    New-Item -Path $b -ItemType HardLink -Target $a -ErrorAction Stop | Out-Null
    $linkedA = Identity $a; $linkedB = Identity $b
    $freeDedupe = Free $mountRoot
    $allocatedDedupe = Allocated $a
    Assert ($linkedA.Index -eq $linkedB.Index -and $linkedA.Links -eq 2 -and
        $linkedB.Links -eq 2 -and (Hash $b) -eq $hash) 'Synthetic hardlink proof failed'
    Assert (($allocatedBefore - $allocatedDedupe) -ge 8MB) 'Unique-ID savings absent'
    Assert (($freeDedupe - $freeBefore) -ge 4MB) 'Isolated VHDX free space did not increase'

    [IO.File]::Copy($b, $stage)
    Assert ((Identity $stage).Index -ne $linkedA.Index) 'Independent stage is a hardlink'
    [IO.File]::Replace($stage, $b, $null)
    $idDetached = Identity $b; $idMaster = Identity $a
    $freeUndone = Free $mountRoot
    Assert ($idDetached.Links -eq 1 -and $idMaster.Links -eq 1 -and
        $idDetached.Index -ne $idMaster.Index -and
        (Hash $a) -eq $hash -and (Hash $b) -eq $hash) 'Independent Undo proof failed'
    Assert (($freeDedupe - $freeUndone) -ge 4MB) 'Undo VHDX reallocation not observed'

    # Fill ONLY the 512-MiB generated volume to cross the 4-MiB reserve.
    $filler = [IO.Path]::Combine($mount, 'generated-only-filler.bin')
    $chunk = [byte[]]::new(1MB)
    [Security.Cryptography.RandomNumberGenerator]::Fill($chunk)
    $fillBytes = 0L
    $stream = [IO.FileStream]::new($filler, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        for ($i = 0; $i -lt 510; $i++) {
            if ((Free $mountRoot) -lt 4MB) { break }
            try {
                $stream.Write($chunk)
                $stream.Flush($true)
                $fillBytes += $chunk.Length
            } catch [IO.IOException] {
                break
            }
        }
    } finally { $stream.Dispose() }
    $freeLow = Free $mountRoot
    Assert ($freeLow -lt 4MB) 'Did not reach generated NTFS low-space boundary'
    Assert ((Hash $a) -eq $hash -and (Hash $b) -eq $hash -and
        (Identity $a).Index -ne (Identity $b).Index) 'Low space changed the original data'
    $report = [ordered]@{
        schema = 'chappy-r217-part29-isolated-vhdx-v1'
        verdict = 'PASS'
        disposableWindowsCi = $true
        vhdxAttached = $true
        isolatedNtfsVerified = $true
        noPreexistingVolumeFormatted = $true
        syntheticInputBytes = $bytes.Length
        independentAllocatedBeforeBytes = $allocatedBefore
        dedupedUniqueAllocatedBytes = $allocatedDedupe
        uniqueFileIdAllocatedSavingsBytes = ($allocatedBefore - $allocatedDedupe)
        freeSpaceBeforeDedupeBytes = $freeBefore
        freeSpaceAfterDedupeBytes = $freeDedupe
        freeSpaceAfterIndependentUndoBytes = $freeUndone
        isolatedFreeSpaceGainVerified = ($freeDedupe - $freeBefore) -ge 4MB
        independentUndoReallocatedSpaceVerified = ($freeDedupe - $freeUndone) -ge 4MB
        lowSpaceReachedBelowFourMiB = $freeLow -lt 4MB
        generatedFillerBytes = $fillBytes
        allSourceHashesAndIndependentIdsPreserved = $true
        productionDedupeEnabled = $false
        productionUndoEnabled = $false
        realUserMediaTouched = $false
        realUserCapacityRecoveredBytes = 0
        physicalPowerCutProven = $false
    }
    [IO.Directory]::CreateDirectory('diagnostics') | Out-Null
    $json = $report | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText([IO.Path]::Combine('diagnostics',
        'r217-part29-isolated-vhdx.json'), $json)
    Write-Host 'R2.17 PART29 isolated mounted NTFS VHDX dedupe, Undo, low-space PASS'
    Write-Host $json
}
finally {
    # Detach only the exact VHDX created under this uniquely generated TEMP.
    # No "select disk", "clean", host drive letter or preexisting partition.
    if ([IO.File]::Exists($image)) {
        try {
            $detach = @(
                "select vdisk file=""$image""",
                'detach vdisk',
                'exit'
            ) -join ([Environment]::NewLine)
            RunDiskpart $detach $detachScript
            $detachSucceeded = $true
        } catch {
            Write-Warning "Generated VHDX detach could not be verified: $_"
        }
    }
    if ($detachSucceeded -or -not [IO.File]::Exists($image)) {
        if ($sandbox.StartsWith($temp + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -and
            [IO.File]::Exists($marker) -and
            [IO.File]::ReadAllText($marker) -eq 'PART29-VHDX-SYNTHETIC:' + $nonce) {
            [IO.Directory]::Delete($sandbox, $true)
        }
    } else {
        Write-Warning 'Generated VHDX retained on ephemeral runner; unsafe cleanup refused'
    }
}

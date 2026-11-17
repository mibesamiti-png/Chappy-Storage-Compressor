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
$assigned = [IO.DriveInfo]::GetDrives().Name
$directLetter = @('Z', 'Y', 'X', 'W', 'V') | Where-Object {
    $assigned -notcontains ("${_}:\")
} | Select-Object -First 1
if (-not $directLetter) { throw 'No unused letter for the disposable VHDX' }

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
    // PowerShell's method binder converts a null third string argument to
    // an empty path on .NET 10. The Core implementation uses an actual null
    // backup path; call the same .NET overload from a typed C# wrapper.
    public static void ReplaceWithoutBackup(string stage, string alias) =>
        System.IO.File.Replace(stage, alias, null);
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
function UniqueAllocation([string[]]$paths) {
    # Count data-bearing NTFS IDs once, regardless of hardlink path count.
    $ids = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    [long]$bytes = 0
    foreach ($path in $paths) {
        if (-not [IO.File]::Exists($path)) { continue }
        $id = Identity $path
        $key = "$($id.Volume):$($id.Index)"
        if ($ids.Add($key)) { $bytes += (Allocated $path) }
    }
    return [pscustomobject]@{ Bytes = $bytes; Ids = $ids.Count }
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
        "assign letter=$directLetter",
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
    [ChappyPart29Native]::ReplaceWithoutBackup($stage, $b)
    $idDetached = Identity $b; $idMaster = Identity $a
    $freeUndone = Free $mountRoot
    Assert ($idDetached.Links -eq 1 -and $idMaster.Links -eq 1 -and
        $idDetached.Index -ne $idMaster.Index -and
        (Hash $a) -eq $hash -and (Hash $b) -eq $hash) 'Independent Undo proof failed'
    Assert (($freeDedupe - $freeUndone) -ge 4MB) 'Undo VHDX reallocation not observed'

    # B2 G2-06: 2/3/2 GROUP PHYSICAL ACCOUNTING ON THE ISOLATED VHDX.
    # This is a native NTFS hardlink/free-space model of the exact stages
    # exercised by the REAL Core process-kill matrix in Part27. It does NOT
    # claim that the WPF or production Core is authorized to traverse a
    # mounted-VHDX directory junction; their safety boundary forbids that.
    $g0a = [IO.Path]::Combine($mount, 'batch0-a.mp4')
    $g0b = [IO.Path]::Combine($mount, 'batch0-b.mp4')
    $g1a = [IO.Path]::Combine($mount, 'batch1-a.mp4')
    $g1b = [IO.Path]::Combine($mount, 'batch1-b.mp4')
    $g1c = [IO.Path]::Combine($mount, 'batch1-c.mp4')
    $g2a = [IO.Path]::Combine($mount, 'batch2-a.mp4')
    $g2b = [IO.Path]::Combine($mount, 'batch2-b.mp4')
    $m0 = [IO.Path]::Combine($mount, 'batch0-master.mp4')
    $m1 = [IO.Path]::Combine($mount, 'batch1-master.mp4')
    $m2 = [IO.Path]::Combine($mount, 'batch2-master.mp4')
    $backup0 = [IO.Path]::Combine($mount, 'batch0-b.saved')
    $backup1 = [IO.Path]::Combine($mount, 'batch1-b.saved')
    $payload = [byte[]]::new(4MB)
    [Security.Cryptography.RandomNumberGenerator]::Fill($payload)
    [IO.File]::WriteAllBytes($g0a, $payload)
    [IO.File]::Copy($g0a, $g0b)
    [Security.Cryptography.RandomNumberGenerator]::Fill($payload)
    [IO.File]::WriteAllBytes($g1a, $payload)
    [IO.File]::Copy($g1a, $g1b)
    [IO.File]::Copy($g1a, $g1c)
    [Security.Cryptography.RandomNumberGenerator]::Fill($payload)
    [IO.File]::WriteAllBytes($g2a, $payload)
    [IO.File]::Copy($g2a, $g2b)
    $names = [string[]]@($g0a, $g0b, $g1a, $g1b, $g1c, $g2a, $g2b)
    $beforeGroup = UniqueAllocation $names
    $freeGroupBaseline = Free $mountRoot
    $g0Hash = Hash $g0a
    $g1Hash = Hash $g1a
    $g2Hash = Hash $g2a
    $id2a = Identity $g2a
    $id2b = Identity $g2b
    Assert ($beforeGroup.Ids -eq 7 -and $beforeGroup.Bytes -eq 28MB) 'VHDX generated three-group baseline does not have seven independently allocated IDs'
    # First group: complete a 2-name transaction. The backing bytes
    # disappear ONLY when the exact independent alias backup is deleted.
    New-Item -Path $m0 -ItemType HardLink -Target $g0a -ErrorAction Stop | Out-Null
    [IO.File]::Move($g0b, $backup0)
    New-Item -Path $g0b -ItemType HardLink -Target $m0 -ErrorAction Stop | Out-Null
    Assert ((Identity $backup0).Index -ne (Identity $g0a).Index -and
        (Hash $backup0) -eq $g0Hash) 'Group 0 preserved independent original before deletion'
    [IO.File]::Delete($backup0)
    $allocatedFirst = UniqueAllocation ($names + @($m0, $m1, $m2, $backup0, $backup1))
    $freeFirst = Free $mountRoot
    Assert ($allocatedFirst.Ids -eq 6 -and
        ($beforeGroup.Bytes - $allocatedFirst.Bytes) -eq 4MB -and
        ($freeFirst - $freeGroupBaseline) -ge 2MB -and
        (Identity $g0a).Links -eq 3 -and (Identity $g0b).Index -eq (Identity $g0a).Index) 'Completed group 0 did not release exactly one NTFS allocation'
    # Group 1: stop after durable journal-equivalent source rename and
    # shared-alias creation, but BEFORE deleting the original backup.
    # A second hardlink to the master does not free backing bytes.
    New-Item -Path $m1 -ItemType HardLink -Target $g1a -ErrorAction Stop | Out-Null
    [IO.File]::Move($g1b, $backup1)
    New-Item -Path $g1b -ItemType HardLink -Target $m1 -ErrorAction Stop | Out-Null
    $allocatedPrepared = UniqueAllocation ($names + @($m0, $m1, $m2, $backup0, $backup1))
    $freePrepared = Free $mountRoot
    Assert ($allocatedPrepared.Ids -eq 6 -and
        ($beforeGroup.Bytes - $allocatedPrepared.Bytes) -eq 4MB -and
        (Identity $backup1).Links -eq 1 -and
        (Identity $backup1).Index -ne (Identity $m1).Index -and
        (Identity $g1b).Index -eq (Identity $m1).Index -and
        (Hash $backup1) -eq $g1Hash) 'Pending original backup must still consume independent space'
    # Group 1: simulated crash immediately AFTER disposing the first
    # backup, BEFORE the group-wide completed receipt. This partially
    # frees physical space, but only group 0 counts as verified complete.
    [IO.File]::Delete($backup1)
    $allocatedPartial = UniqueAllocation ($names + @($m0, $m1, $m2, $backup0, $backup1))
    $freePartial = Free $mountRoot
    $physicalPartialDelta = $beforeGroup.Bytes - $allocatedPartial.Bytes
    $verifiedReceiptLogical = 4MB
    Assert ($allocatedPartial.Ids -eq 5 -and
        $physicalPartialDelta -eq 8MB -and
        ($freePartial - $freeFirst) -ge 2MB -and
        (Identity $g1a).Links -eq 3 -and
        (Identity $g1b).Index -eq (Identity $g1a).Index -and
        (Identity $g1c).Links -eq 1 -and
        (Identity $g1c).Index -ne (Identity $g1a).Index -and
        -not [IO.File]::Exists($backup1) -and
        $verifiedReceiptLogical -lt $physicalPartialDelta) 'Uncommitted group 1 physical savings must not be falsely counted as verified completed savings'
    # Third group was never approached: both original independent IDs,
    # hashes, and link counts must remain EXACTLY unchanged.
    $thirdOriginals = ((Identity $g2a).Index -eq $id2a.Index -and
        (Identity $g2b).Index -eq $id2b.Index -and
        (Identity $g2a).Links -eq 1 -and
        (Identity $g2b).Links -eq 1 -and
        (Hash $g2a) -eq $g2Hash -and
        (Hash $g2b) -eq $g2Hash -and
        -not [IO.File]::Exists($m2))
    Assert $thirdOriginals 'Untouched third group lost original bytes or independent IDs'
    $batchReport = [ordered]@{
        groups = '2/3/2'
        generatedOriginalCount = 7
        generatedFileBytesEach = 4MB
        sourceUniqueIdsBefore = $beforeGroup.Ids
        sourceAllocatedBeforeBytes = $beforeGroup.Bytes
        completedGroup0UniqueIds = $allocatedFirst.Ids
        completedGroup0AllocatedBytes = $allocatedFirst.Bytes
        pendingBackupKeptUniqueIds = $allocatedPrepared.Ids
        pendingBackupKeptAllocatedBytes = $allocatedPrepared.Bytes
        incompleteGroup1AfterBackupDeleteUniqueIds = $allocatedPartial.Ids
        incompleteGroup1AfterBackupDeleteAllocatedBytes = $allocatedPartial.Bytes
        physicalUniqueIdAllocatedDeltaBytes = $physicalPartialDelta
        verifiedCompletedGroupLogicalBytes = $verifiedReceiptLogical
        freeSpaceBeforeGroupsBytes = $freeGroupBaseline
        freeSpaceAfterGroup0Bytes = $freeFirst
        freeSpaceWithGroup1BackupBytes = $freePrepared
        freeSpaceAfterGroup1PartialBytes = $freePartial
        confirmedGroup0FreeSpaceGain = ($freeFirst - $freeGroupBaseline) -ge 2MB
        observedPartialFreeSpaceGain = ($freePartial - $freeFirst) -ge 2MB
        pendingBackupConsumesStorage = $allocatedPrepared.Bytes -eq $allocatedFirst.Bytes
        unfinishedGroupPhysicalSavingsNotCreditedToReceipt = $verifiedReceiptLogical -lt $physicalPartialDelta
        untouchedThirdGroupIndependentIdsAndSha = $thirdOriginals
        realCoreOnMountedJunction = $false
        physicalPowerCutProven = $false
    }

    # G4-06: the C# smoke test ran both images through the real Core.
    # This native replay reuses those bytes on the isolated mount, where the
    # Core intentionally forbids directory junctions.
    $fixture = [IO.Path]::Combine($temp, 'chappy-g4-generated-jpeg-pair')
    $proof = Get-Content -Raw -LiteralPath 'diagnostics/g4-jpeg-ntfs-allocation.json' |
        ConvertFrom-Json
    $originalFixture = [IO.Path]::Combine($fixture, 'original.jpg')
    $candidateFixture = [IO.Path]::Combine($fixture, 'candidate.jpg')
    Assert (([IO.FileInfo]::new($originalFixture)).Length -eq $proof.originalLogicalBytes -and
        ([IO.FileInfo]::new($candidateFixture)).Length -eq $proof.candidateLogicalBytes) 'Unexpected G4 JPEG fixture'
    $jpeg = [IO.Path]::Combine($mount, 'generated-g4.jpg')
    $jpegStage = [IO.Path]::Combine($mount, 'generated-g4.stage')
    $jpegRollback = [IO.Path]::Combine($mount, 'generated-g4.rollback')
    $jpegQuarantine = [IO.Path]::Combine($mount, 'generated-g4.quarantine')
    [IO.File]::Copy($originalFixture, $jpeg)
    [IO.File]::Copy($candidateFixture, $jpegStage)
    $jpegFreePrepared = Free $mountRoot
    [IO.File]::Move($jpeg, $jpegRollback)
    [IO.File]::Move($jpegStage, $jpeg)
    $jpegFreeApplied = Free $mountRoot
    Assert ((Hash $jpeg) -eq (Hash $candidateFixture) -and
        (Hash $jpegRollback) -eq (Hash $originalFixture)) 'G4 JPEG swap changed generated bytes'
    [IO.File]::Delete($jpegRollback) # Simulated loss of this exact generated rollback only.
    $jpegFreeWithoutRollback = Free $mountRoot
    [IO.File]::Move($jpeg, $jpegQuarantine)
    [IO.File]::Copy($originalFixture, $jpeg)
    $jpegFreeRestored = Free $mountRoot
    Assert ((Hash $jpeg) -eq (Hash $originalFixture) -and
        (Hash $jpegQuarantine) -eq (Hash $candidateFixture)) 'G4 JPEG restore changed generated bytes'
    $jpegGain = $jpegFreeWithoutRollback - $jpegFreeApplied
    $jpegRestoreCost = $jpegFreeWithoutRollback - $jpegFreeRestored
    Assert ([Math]::Abs($jpegFreeApplied - $jpegFreePrepared) -lt ($proof.originalLogicalBytes / 2) -and
        $jpegGain -ge ($proof.originalLogicalBytes / 2) -and
        $jpegRestoreCost -ge ($proof.originalLogicalBytes / 2)) 'G4 isolated JPEG free space did not follow retained data'
    $jpegReport = [ordered]@{
        originalLogicalBytes = $proof.originalLogicalBytes
        candidateLogicalBytes = $proof.candidateLogicalBytes
        freePreparedBytes = $jpegFreePrepared
        freeAppliedWithRollbackBytes = $jpegFreeApplied
        freeWithoutRollbackBytes = $jpegFreeWithoutRollback
        freeRestoredWithQuarantineBytes = $jpegFreeRestored
        rollbackRemovalObservedGainBytes = $jpegGain
        backupRestoreObservedCostBytes = $jpegRestoreCost
        nativeReplayOnIsolatedVhdx = $true
    }

    # Reuse the same VHDX under its verified direct drive letter so the
    # actual WPF/Core path is exercised without following a mount junction.
    $direct = "${directLetter}:\"
    Assert ((VolumeName $direct) -eq $testVolume) 'Direct letter does not address the generated VHDX'
    $isolatedTemp = [IO.Path]::Combine($direct, 'v1-generated-temp')
    [IO.Directory]::CreateDirectory($isolatedTemp) | Out-Null
    $uiDll = [IO.Path]::GetFullPath('tests/bin/Release/net10.0-windows/ChappyStorageCompressor.UiSmokeTests.dll')
    Assert ([IO.File]::Exists($uiDll)) 'Authoritative WPF test binary was not built'
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.ArgumentList.Add($uiDll)
    $start.ArgumentList.Add('--v1-isolated')
    $start.Environment['TEMP'] = $isolatedTemp
    $start.Environment['TMP'] = $isolatedTemp
    $start.Environment['CHAPPY_V1_VHDX_TEMP'] = $isolatedTemp
    $worker = [Diagnostics.Process]::Start($start)
    try {
        if (-not $worker.WaitForExit(300000)) {
            $worker.Kill($true)
            $worker.WaitForExit()
            throw 'Isolated WPF acceptance timed out'
        }
        Assert ($worker.ExitCode -eq 0) "Isolated WPF acceptance exit: $($worker.ExitCode)"
    } finally { $worker.Dispose() }
    $wpf = Get-Content -Raw -LiteralPath 'diagnostics/v1-isolated-wpf.json' | ConvertFrom-Json
    Assert ($wpf.rollbackRemovalObservedGainBytes -gt 0 -and
        $wpf.backupRestoreObservedCostBytes -gt 0 -and
        $wpf.realUserCapacityRecoveredBytes -eq 0) 'Isolated WPF allocation proof failed'

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
        groupedPartialAllocation = $batchReport
        imageReplacementAllocation = $jpegReport
        actualWpfCoreIsolatedVolume = $wpf
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

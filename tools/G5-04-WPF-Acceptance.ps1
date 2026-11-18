# G5-04 human-operated WPF acceptance fixture, NOT an automated PASS.
# Windows PowerShell 5.1+. Creates new, disposable data ONLY under %TEMP%.
# No operation here moves, deletes, replaces, or restores scanned originals.
param(
    [switch]$StartApp,
    [switch]$PrepareOnly,
    [switch]$VerifyOnly,
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($VerifyOnly -and ($StartApp -or $PrepareOnly)) {
    throw 'VerifyOnly cannot be combined with fixture creation or app launch.'
}
if ($PrepareOnly -and $StartApp) {
    throw 'PrepareOnly cannot launch the app.'
}

$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    if ($VerifyOnly) { throw 'Specify the generated -OutputRoot to verify.' }
    $OutputRoot = Join-Path $temp ('ChappyStorageManualG5_' + [Guid]::NewGuid().ToString('N'))
}
$root = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
$parent = [IO.Path]::GetDirectoryName($root).TrimEnd('\')
$name = [IO.Path]::GetFileName($root)
if (-not $parent.Equals($temp, [StringComparison]::OrdinalIgnoreCase) -or
    -not $name.StartsWith('ChappyStorageManualG5_', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'G5 fixture root must be a new ChappyStorageManualG5_* directory directly under Windows TEMP.'
}
$sentinel = Join-Path $root '.chappy-g5-generated-only'
$baseline = Join-Path $root 'BASELINE.json'
$dataset = Join-Path $root 'dataset'

function Get-PayloadFiles {
    param([string]$DatasetRoot)
    @(Get-ChildItem -LiteralPath $DatasetRoot -File -Recurse -Force -ErrorAction Stop |
        Sort-Object FullName)
}
function Verify-GeneratedFixture {
    if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf) -or
        (Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'G5-04 generated NTFS fixture only' -or
        -not (Test-Path -LiteralPath $baseline -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dataset -PathType Container)) {
        throw 'Unrecognized or incomplete generated fixture; refusing verification.'
    }
    $record = Get-Content -LiteralPath $baseline -Raw | ConvertFrom-Json
    if ($record.Schema -ne 1 -or $record.HumanWpfPassed -ne $false -or
        $record.ProductionWriteAuthorized -ne $false -or
        $record.Files.Count -lt 100) {
        throw 'Invalid fixture baseline metadata.'
    }
    $actual = Get-PayloadFiles $dataset
    if ($actual.Count -ne $record.Files.Count) {
        throw "Generated dataset file count changed: expected $($record.Files.Count), actual $($actual.Count)."
    }
    $known = @{}
    foreach ($entry in $record.Files) {
        $relative = [string]$entry.RelativePath
        if ([IO.Path]::IsPathRooted($relative) -or
            $relative.Split('\') -contains '..' -or
            $relative.Split('\') -contains '.' -or
            [string]::IsNullOrWhiteSpace($relative)) {
            throw 'Unsafe relative path in fixture metadata.'
        }
        $path = [IO.Path]::GetFullPath((Join-Path $dataset $relative))
        if (-not $path.StartsWith($dataset + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Fixture metadata escaped generated directory.'
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Missing or unsafe fixture item: $relative"
        }
        $observed = Get-Item -LiteralPath $path
        $sha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($observed.Length -ne [long]$entry.Bytes -or
            $sha -ne [string]$entry.Sha256) {
            throw "Generated file changed: $relative"
        }
        if ($known.ContainsKey($relative)) { throw 'Duplicate manifest entry.' }
        $known[$relative] = $true
    }
    foreach ($file in $actual) {
        $relative = $file.FullName.Substring($dataset.Length + 1)
        if (-not $known.ContainsKey($relative)) {
            throw "Unexpected new file in generated input: $relative"
        }
    }
    Write-Host "G5_DATA_INTACT: $($actual.Count) generated files, SHA-256 and sizes unchanged."
    Write-Host 'G5_HUMAN_WPF_ACCEPTANCE: NOT_ASSESSED (manual operation and sign-off required).'
    Write-Host "G5_DATASET=$dataset"
}
if ($VerifyOnly) {
    Verify-GeneratedFixture
    return
}
if (Test-Path -LiteralPath $root) {
    throw 'Fixture destination already exists. No existing directory will be reused or overwritten.'
}
[void][IO.Directory]::CreateDirectory($root)
Set-Content -LiteralPath $sentinel -Value 'G5-04 generated NTFS fixture only' -Encoding UTF8
[void][IO.Directory]::CreateDirectory($dataset)
foreach ($relative in @('Keep','Copies\A','Copies\B','Copies\C','Unique','同名ファイル')) {
    [void][IO.Directory]::CreateDirectory((Join-Path $dataset $relative))
}
# The collision folder and root memo are deliberately FOREIGN EXISTING files.
# They must never be replaced, moved, or automatically deleted by product UI.
Set-Content -LiteralPath (Join-Path $dataset '同名ファイル\existing.txt') -Value 'Existing user-like item: never overwrite' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $dataset '同名ファイル一覧.txt') -Value 'Existing user-like report: never overwrite' -Encoding UTF8

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap(960, 540)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([System.Drawing.Color]::FromArgb(36, 82, 170))
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 190, 46))
    try {
        $graphics.FillEllipse($brush, 220, 85, 510, 350)
    }
    finally { $brush.Dispose() }
    $png = Join-Path $dataset 'Keep\generated.png'
    $jpg = Join-Path $dataset 'Keep\generated.jpg'
    $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Save($jpg, [System.Drawing.Imaging.ImageFormat]::Jpeg)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
Copy-Item -LiteralPath (Join-Path $dataset 'Keep\generated.png') -Destination (Join-Path $dataset 'Copies\A\generated.png')
Copy-Item -LiteralPath (Join-Path $dataset 'Keep\generated.png') -Destination (Join-Path $dataset 'Copies\B\generated.png')
Copy-Item -LiteralPath (Join-Path $dataset 'Keep\generated.jpg') -Destination (Join-Path $dataset 'Copies\A\generated.jpg')
Copy-Item -LiteralPath (Join-Path $dataset 'Keep\generated.jpg') -Destination (Join-Path $dataset 'Copies\B\generated.jpg')

Set-Content -LiteralPath (Join-Path $dataset 'Keep\notes.txt') -Value 'G5 acceptance synthetic duplicate content' -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $dataset 'Keep\notes.txt') -Destination (Join-Path $dataset 'Copies\C\notes.txt')
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $large = New-Object byte[] (8 * 1024 * 1024)
    $rng.GetBytes($large)
    $big = Join-Path $dataset 'Keep\big-sample.bin'
    [IO.File]::WriteAllBytes($big, $large)
    Copy-Item -LiteralPath $big -Destination (Join-Path $dataset 'Copies\A\big-sample.bin')
    Copy-Item -LiteralPath $big -Destination (Join-Path $dataset 'Copies\B\big-sample.bin')
    for ($i = 0; $i -lt 160; $i++) {
        $small = New-Object byte[] 12288
        $rng.GetBytes($small)
        $path = Join-Path $dataset ('Unique\unique-{0:D4}.bin' -f $i)
        [IO.File]::WriteAllBytes($path, $small)
    }
}
finally { $rng.Dispose() }

$files = @(Get-PayloadFiles $dataset)
$manifest = @(
    foreach ($file in $files) {
        [ordered]@{
            RelativePath = $file.FullName.Substring($dataset.Length + 1)
            Bytes = [long]$file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
)
[ordered]@{
    Schema = 1
    CreatedUtc = [DateTime]::UtcNow.ToString('o')
    DatasetPath = $dataset
    FileCount = $files.Count
    Files = $manifest
    HumanWpfPassed = $false
    ProductionWriteAuthorized = $false
    Notes = 'Hash baseline is local convenience only. It is not an independent security attestation.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $baseline -Encoding UTF8

$instructions = @'
G5-04 — 人が操作するWPF受入（未実施・結果を自動PASSにしない）
1. ChappyStorageCompressor.exe を開き、「重複整理」で下の DATASET を選択。
2. 検索・停止・再検索、重複グループのダブルクリック、画像プレビューをマウスで確認。
3. Keeperを1件選び「整理先・削除候補を確認」で全パス、候補の確認、最終照合を確認。
4. 「照合済みの確認を保存」→ 閉じて再スキャン →「保存した確認を復元」を確認。
5. 既存の「同名ファイル」フォルダと「同名ファイル一覧.txt」を上書きしない整理先プレビューを確認。
6. 削除・実ファイル保留・ごみ箱・復元ボタンが本番で無効なことを確認。
7. 「画像圧縮」「履歴・復元」タブを操作し、警告・説明・取消と画面表示を点検。
8. アプリを閉じ、-VerifyOnly で元データ全件のSHA-256/サイズ/件数を再検証。
9. PASS/FAIL、Windowsバージョン、アプリBUILD-MANIFEST SHA、日時、画面上の問題点を
   Issue #54 に人間が明示的に報告する。自動実行だけではG5-04を合格扱いにしない。

対応外：有効なMP4動画による再生・サムネイルの受入、実機電源断、通常EXEで
無効化中の原本Apply/Undoや実ごみ箱削除。これらを実施済みと記録しない。

実験の入力は生成フォルダのみ。一般ファイルを選択しない。
生成フォルダをスキャンしても容量は回復しない。ユーザーファイルを一切削除しない。
'@
$instructions += [Environment]::NewLine + "DATASET: $dataset" + [Environment]::NewLine +
    "BASELINE: $baseline" + [Environment]::NewLine +
    "VERIFY: powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""$PSCommandPath"" -VerifyOnly -OutputRoot ""$root""" + [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $root 'G5-04-CHECKLIST.txt') -Value $instructions -Encoding UTF8
Verify-GeneratedFixture
Write-Host "G5_ROOT=$root"
Write-Host "G5_CHECKLIST=$(Join-Path $root 'G5-04-CHECKLIST.txt')"
if ($StartApp) {
    $app = Join-Path $PSScriptRoot 'ChappyStorageCompressor.exe'
    if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
        throw 'Packaged WPF application was not found next to this fixture tool.'
    }
    Start-Process -FilePath $app -WorkingDirectory $PSScriptRoot
    Write-Host 'WPF app launched; use the generated DATASET shown above.'
}

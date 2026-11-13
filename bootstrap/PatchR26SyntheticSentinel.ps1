param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Only patch the extracted, verified R2.6 SourceKit in CI. This source code
# does not run in the production WPF application (CHAPPY_TESTING only).
$resolved = (Resolve-Path -LiteralPath $SourcePath).Path
$source = [System.IO.File]::ReadAllText($resolved)
$before = @'
        using var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] text = System.Text.Encoding.ASCII.GetBytes("CHAPPY-R23-SYNTHETIC-ONLY\n");
        stream.Write(text);
        stream.Flush(true);
        ValidateIsolatedDirectory(full);
'@
$after = @'
        using (var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] text = System.Text.Encoding.ASCII.GetBytes("CHAPPY-R23-SYNTHETIC-ONLY\n");
            stream.Write(text);
            stream.Flush(true);
        }
        ValidateIsolatedDirectory(full);
'@
if (-not $source.Contains($before) -or $source.IndexOf($before) -ne $source.LastIndexOf($before)) {
    throw "R2.6 synthetic test-only patch context mismatch; refusing to modify"
}
[System.IO.File]::WriteAllText($resolved, $source.Replace($before, $after), [System.Text.UTF8Encoding]::new($false))
Write-Host "Patched test-only sentinel handle lifetime"

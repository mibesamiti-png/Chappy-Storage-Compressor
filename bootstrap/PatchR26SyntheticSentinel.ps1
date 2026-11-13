param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# CI-only patch to the already SHA-256 verified and extracted R2.6 test source.
# The source file holds an exclusive sentinel handle until its method exits,
# then reads it back while still locked. Close the handle before validation.
$resolved = (Resolve-Path -LiteralPath $SourcePath).Path
$source = [System.IO.File]::ReadAllText($resolved)
$needle = '        ValidateIsolatedDirectory(full);'
$occurrences = $source.Split(@($needle), [StringSplitOptions]::None).Length - 1
if ($occurrences -ne 1 -or -not $source.Contains('using var stream = new FileStream(sentinel,')) {
    throw "R2.6 synthetic-only patch context mismatch; refusing to modify"
}
$source = $source.Replace($needle, '        stream.Dispose();' + [Environment]::NewLine + $needle)
[System.IO.File]::WriteAllText($resolved, $source, [System.Text.UTF8Encoding]::new($false))
Write-Host "Patched R2.6 synthetic test: close sentinel stream before checking"

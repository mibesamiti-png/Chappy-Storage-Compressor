#requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$version = '1.0.0-rc.1'
$dependency = '7b4993786d856cbccabfc0ed2f923ba000e326cc'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:CHAPPY_R217_JPEGTRAN_COMMIT -ne $dependency) {
    throw 'Candidate packaging requires the source-pinned Windows CI dependency.'
}
$dist = Join-Path $PSScriptRoot '../dist/win-x64'
$app = Join-Path $dist 'ChappyStorageCompressor.exe'
$jpeg = Join-Path $dist 'jpegtran.exe'
$license = Join-Path $env:RUNNER_TEMP 'chappy-r217-official-jpegtran/libjpeg-turbo/LICENSE.md'
$readme = Join-Path $PSScriptRoot '../README.md'
$manualFixtureTool = Join-Path $PSScriptRoot '../tools/G5-04-WPF-Acceptance.ps1'
$manualLauncher = Join-Path $PSScriptRoot '../tools/RUN-WPF-MANUAL-CHECK.cmd'
$commit = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Candidate source commit could not be verified.'
}
$observedVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
if ($observedVersion -ne $version -and -not $observedVersion.StartsWith("$version+")) {
    throw "Unexpected executable product version: $observedVersion"
}
if ((Get-FileHash -LiteralPath $jpeg -Algorithm SHA256).Hash -ne $env:CHAPPY_R217_SIMD_SHA256) {
    throw 'Bundled jpegtran failed verification against the compiled pin.'
}

Copy-Item -LiteralPath $license -Destination (Join-Path $dist 'libjpeg-turbo-LICENSE.md')
Copy-Item -LiteralPath $readme -Destination (Join-Path $dist 'README.md')
Copy-Item -LiteralPath $manualFixtureTool -Destination (Join-Path $dist 'G5-04-WPF-Acceptance.ps1')
Copy-Item -LiteralPath $manualLauncher -Destination (Join-Path $dist 'RUN-WPF-MANUAL-CHECK.cmd')
$files = @(
    'ChappyStorageCompressor.exe', 'jpegtran.exe',
    'libjpeg-turbo-LICENSE.md', 'README.md',
    'G5-04-WPF-Acceptance.ps1', 'RUN-WPF-MANUAL-CHECK.cmd'
)
$checksums = foreach ($name in $files) {
    $digest = (Get-FileHash -LiteralPath (Join-Path $dist $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  $name"
}
$checksums | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding utf8
[ordered]@{
    version = $version
    executableProductVersion = $observedVersion
    sourceCommit = $commit
    target = 'win-x64'
    jpegtranVersion = '3.2.1'
    jpegtranSourceCommit = $dependency
    appAuthenticodeStatus = (Get-AuthenticodeSignature -LiteralPath $app).Status.ToString()
    jpegtranAuthenticodeStatus = (Get-AuthenticodeSignature -LiteralPath $jpeg).Status.ToString()
    checksumFile = 'SHA256SUMS.txt'
    productionMutationAuthorized = $false
    officialRelease = $false
    humanWpfAcceptance = 'NOT_RUN'
    manualAcceptanceKit = 'G5-04-WPF-Acceptance.ps1'
    manualAcceptanceLauncher = 'RUN-WPF-MANUAL-CHECK.cmd'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'BUILD-MANIFEST.json') -Encoding utf8

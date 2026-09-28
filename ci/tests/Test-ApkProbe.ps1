$ErrorActionPreference = 'Stop'
$probe = Join-Path (Split-Path $PSScriptRoot) 'Probe-DirectApk.ps1'
$saved = $env:GITHUB_OUTPUT
$output = [IO.Path]::GetTempFileName()
$env:GITHUB_OUTPUT = $output
$global:ApkProbeTestEtag = '"v1"'
function Invoke-WebRequest {
    [pscustomobject]@{ Headers = @{ ETag=$global:ApkProbeTestEtag; 'Content-Length'='100'; 'x-amz-version-id'='version'; 'Last-Modified'='fixed' } }
}
function Probe {
    Clear-Content $output
    & $probe -Url 'https://example.com/game'
    (Get-Content $output | Where-Object { $_.StartsWith('key=') })
}
try {
    $first = Probe
    if ($first -ne (Probe) -or $first -notmatch '^key=dmm-apk-v1-[a-f0-9]{64}$') { throw 'Stable metadata key failed' }
    $global:ApkProbeTestEtag = '"v2"'
    if ($first -eq (Probe)) { throw 'Changed metadata must invalidate cache' }
    $global:ApkProbeTestEtag = 'W/"v2"'
    if ((Probe) -ne 'key=') { throw 'Weak ETag must disable cache' }
    $global:ApkProbeTestEtag = ''
    if ((Probe) -ne 'key=') { throw 'Missing ETag must disable cache' }
    Write-Host 'PASS: stable, changed, weak and missing ETag cache decisions'
} finally {
    Remove-Variable ApkProbeTestEtag -Scope Global -ErrorAction SilentlyContinue
    $env:GITHUB_OUTPUT = $saved
    Remove-Item -LiteralPath $output
}

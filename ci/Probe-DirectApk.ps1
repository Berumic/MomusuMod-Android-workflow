param([Parameter(Mandatory)][string]$Url)
$ErrorActionPreference = 'Stop'
if (([Uri]$Url).Scheme -ne 'https') { throw 'APK URL must use HTTPS.' }
$etag = ''
$key = ''
try {
    $response = Invoke-WebRequest -Uri $Url -Method Head -TimeoutSec 30 -MaximumRedirection 10
    $etag = ($response.Headers['ETag'] -join '').Trim()
    $length = $response.Headers['Content-Length'] -join ''
    $version = $response.Headers['x-amz-version-id'] -join ''
    $modified = $response.Headers['Last-Modified'] -join ''
    if (!$etag -or $etag.StartsWith('W/') -or $length -notmatch '^[1-9][0-9]*$') {
        throw 'No strong ETag / content length; download without caching.'
    }
    $identity = @($Url, $etag, $length, $version, $modified) | ConvertTo-Json -Compress
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
    $key = 'dmm-apk-v1-' + $hash
    Write-Host "DMM HEAD: bytes=$length; ETag=$etag; Last-Modified=$modified"
} catch {
    Write-Warning "APK metadata probe unavailable: $($_.Exception.Message)"
    $etag = ''
}
if ($env:GITHUB_OUTPUT) {
    "key=$key" | Add-Content $env:GITHUB_OUTPUT
    "etag=$etag" | Add-Content $env:GITHUB_OUTPUT
}

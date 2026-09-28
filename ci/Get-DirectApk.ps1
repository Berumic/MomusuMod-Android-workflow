param(
    [Parameter(Mandatory)][string]$Url,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$uri = [Uri]$Url
if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') { throw 'APK URL must use HTTPS.' }
$destination = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $destination) { throw 'APK output already exists.' }
New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
$partial = $destination + '.' + [guid]::NewGuid().ToString('N') + '.partial'
try {
    # Follow DMM redirects; save to a fixed name regardless of Content-Disposition.
    & curl.exe --fail --location --proto '=https' --proto-redir '=https' --retry 3 --retry-delay 5 --connect-timeout 30 --max-time 1800 --output $partial $Url
    if ($LASTEXITCODE -ne 0) { throw 'DMM APK download failed; check runner network access and HTTP status above.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($partial)
    try {
        if (!$archive.GetEntry('AndroidManifest.xml')) { throw 'Downloaded file is not an APK (AndroidManifest.xml missing).' }
        if (!$archive.GetEntry('lib/arm64-v8a/libil2cpp.so')) { throw 'Downloaded APK is not the expected ARM64 IL2CPP game.' }
    } finally { $archive.Dispose() }
    Move-Item -LiteralPath $partial -Destination $destination
    Write-Host "APK downloaded: $((Get-Item -LiteralPath $destination).Length) bytes; SHA256=$((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash)"
    # Existing Build-Identity / Build-Apk steps validate package and versionCode.
} finally {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
}

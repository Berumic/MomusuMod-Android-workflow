param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [string]$AssetName,
    [Parameter(Mandatory)][string]$Output,
    [string]$Sha256
)
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
$escaped = [Uri]::EscapeDataString($Tag)
$json = & gh api "repos/$Repository/releases/tags/$escaped"
if ($LASTEXITCODE -ne 0) { throw "Cannot read release '$Tag'." }
$release = ($json -join "`n") | ConvertFrom-Json
$AssetName = if ($null -ne $AssetName) { $AssetName.Trim() } else { '' }
$assets = @($release.assets | Where-Object {
    if ($AssetName) { $_.name -ieq $AssetName } else { $_.name -match '(?i)\.apk$' }
})
if ($AssetName -and $assets.Count -eq 0) {
    # Some GitHub API responses may omit release assets even though gh release
    # download can resolve them. Use the explicitly supplied filename directly.
    $assets = @([pscustomobject]@{ name = $AssetName })
}
if ($assets.Count -ne 1) {
    $available = @($release.assets | ForEach-Object { $_.name }) -join ', '
    if ($AssetName) {
        throw "APK asset '$AssetName' was not found in release '$Tag'. Available assets: $available"
    }
    throw "Source release '$Tag' contains $($assets.Count) APK assets. Specify apk_asset_name exactly. APK assets: $available"
}
$parent = Split-Path ([IO.Path]::GetFullPath($Output))
New-Item -ItemType Directory -Force $parent | Out-Null
$temp = Join-Path $parent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null
try {
    $asset = $assets[0]
    $downloadPath = Join-Path $temp $asset.name
    $downloaded = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & gh release download $Tag --repo $Repository --pattern $asset.name --dir $temp --clobber
        if ($LASTEXITCODE -eq 0) { $downloaded = $true; break }
        if ($attempt -lt 3) { Start-Sleep -Seconds (2 * $attempt) }
    }
    if (!$downloaded) {
        # Public download endpoint: never forward the Actions token to the CDN.
        Write-Warning 'API download failed; trying the public release download URL.'
        $encodedName = [Uri]::EscapeDataString($asset.name)
        $publicUrl = "https://github.com/$Repository/releases/download/$escaped/$encodedName"
        & curl.exe --fail --location --retry 3 --retry-delay 2 --connect-timeout 30 --max-time 1800 --output $downloadPath $publicUrl
        if ($LASTEXITCODE -ne 0) { throw "Release asset download failed via API and public URL: $Tag / $($asset.name)." }
    }
    $downloads = @(Get-ChildItem $temp -File)
    if ($downloads.Count -ne 1 -or $downloads[0].Name -cne $assets[0].name) { throw 'Ambiguous asset download.' }
    Move-Item -LiteralPath $downloads[0].FullName -Destination $Output
    if ($Sha256 -and (Get-FileHash $Output).Hash -ne $Sha256) { throw 'Toolchain SHA256 mismatch.' }
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}

param(
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$Translations,
    [Parameter(Mandatory)][string]$FontBundle
)
$ErrorActionPreference = 'Stop'
& dotnet build "$PSScriptRoot\MonsterMusumeTDMod.Android.csproj" -c Release "-p:AndroidWorkspace=$Workspace" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
if (!(Test-Path -LiteralPath $FontBundle)) { throw 'Build the Android font AssetBundle first (Build-Fonts.ps1).' }
$manifestPath = Join-Path $Translations 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported translation manifest.' }
# Each build gets a new staging directory; old release files are never silently merged.
$deployment = Join-Path $Workspace ('Deployment-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$mods = Join-Path $deployment 'Mods'
$data = Join-Path $deployment 'UserData'
$target = Join-Path $data 'MonsterMusumeTDMod\translations'
New-Item -ItemType Directory -Force -Path $mods,$target | Out-Null
Copy-Item "$Workspace\Build\MonsterMusumeTDMod.Android.dll" $mods
Copy-Item -LiteralPath "$PSScriptRoot\README.md" -Destination (Join-Path $deployment 'README.md')
foreach ($entry in $manifest.files.PSObject.Properties) {
    $relative = $entry.Name.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Split([IO.Path]::DirectorySeparatorChar) -contains '..') {
        throw "Unsafe translation path: $relative"
    }
    $source = Join-Path $Translations $relative
    $bytes = [IO.File]::ReadAllBytes($source)
    if ((Get-Item -LiteralPath $source).Length -ne $entry.Value.size -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.Value.sha256) {
        # Git may check out JSON as CRLF; the published manifest hashes LF bytes.
        $bytes = [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($source).Replace("`r`n", "`n"))
        if ($bytes.Length -ne $entry.Value.size -or
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $entry.Value.sha256) {
            throw "Translation manifest mismatch (not just Git line endings): $relative"
        }
    }
    $destination = Join-Path $target $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    [IO.File]::WriteAllBytes($destination, $bytes)
}
Copy-Item -LiteralPath $manifestPath -Destination $target
Copy-Item -LiteralPath $FontBundle -Destination (Join-Path $data 'alimama-android')
$fontManifest = @{
    schemaVersion = 1
    generatedAt = [DateTime]::UtcNow.ToString('o')
    commit = 'android-' + (Get-FileHash -LiteralPath $FontBundle).Hash.ToLowerInvariant().Substring(0,12)
    files = @{ 'alimama-android' = @{
        sha256 = (Get-FileHash -LiteralPath $FontBundle).Hash.ToLowerInvariant()
        size = (Get-Item -LiteralPath $FontBundle).Length
    } }
} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $data 'MonsterMusumeTDMod\assets-manifest.json'), $fontManifest)
$publish = Join-Path $Workspace 'Publish\assets\android'
New-Item -ItemType Directory -Force -Path $publish | Out-Null
Copy-Item -LiteralPath $FontBundle -Destination (Join-Path $publish 'alimama-android') -Force
[IO.File]::WriteAllText((Join-Path $publish 'manifest.json'), $fontManifest)
Compress-Archive -Path "$deployment\*" -DestinationPath "$deployment.zip"
Write-Output "Deployment: $deployment"
Write-Output "Mod package: $deployment.zip"
Write-Output "GitHub font update files: $publish"

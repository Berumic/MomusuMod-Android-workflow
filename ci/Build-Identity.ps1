param(
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$Aapt,
    [Parameter(Mandatory)][string]$TranslationsRoot,
    [string]$ExpectedGame = 'auto'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$badging = & $Aapt dump badging $Apk
if ($LASTEXITCODE -ne 0) { throw 'Cannot read APK metadata.' }
$line = ($badging | Where-Object { $_ -match '^package:' }) -join ''
if ($line -notmatch "^package: name='([^']+)' versionCode='([0-9]+)' versionName='([^']*)'") { throw 'Invalid APK package metadata.' }
$package = $Matches[1]; $code = $Matches[2]; $version = $Matches[3]
$targets = (Get-Content "$PSScriptRoot/targets.json" -Raw | ConvertFrom-Json).targets
$matching = @($targets | Where-Object { $_.sourcePackage -ceq $package })
if ($matching.Count -ne 1) { throw "Unsupported source package: $package" }
$target = $matching[0]
if ($ExpectedGame -ne 'auto' -and $ExpectedGame -cne $target.game) { throw "Uploaded APK does not belong to target $ExpectedGame" }
if ($target.game -notmatch '^[a-z0-9-]+$') { throw 'Invalid game slug.' }
# Hash actual source/recipe bytes, including uncommitted local edits.
$paths = @()
foreach ($dir in @('Android','Core','Services','Patches','ci','.github/workflows')) {
    $paths += Get-ChildItem "$root/$dir" -File -Recurse | Where-Object {
        $_.FullName -notmatch '[/\\](bin|obj|__pycache__)[/\\]' -and $_.Name -ne 'targets.json' -and $_.Extension -in @('.cs','.csproj','.ps1','.py','.json','.yml','.md')
    }
}
$records = @($paths | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/')
    $bytes = [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($_.FullName).Replace("`r`n","`n"))
    "$relative=$([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)))"
})
$recipeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($records -join "`n")))).ToLowerInvariant()
$apkHash = (Get-FileHash -LiteralPath $Apk).Hash.ToLowerInvariant()
$manifestText = [IO.File]::ReadAllText("$TranslationsRoot/translations/manifest.json").Replace("`r`n","`n")
$translationsHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifestText))).ToLowerInvariant()
$targetRecipe = $target | ConvertTo-Json -Compress
$inputText = "$targetRecipe|$code|$apkHash|$recipeHash|$translationsHash"
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($inputText))).ToLowerInvariant()
[pscustomobject]@{
    game=$target.game; sourcePackage=$package; modPackage=$target.modPackage; label=$target.label
    sourceVersion=$code; versionName=$version; sourceApkSha256=$apkHash
    recipeSha256=$recipeHash; translationManifestSha256=$translationsHash
    fingerprint=$fingerprint; releaseTag="mod-$($target.game)-v$code"
}

param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$Aapt,
    [Parameter(Mandatory)][string]$TranslationsRoot,
    [string]$ExpectedGame = 'auto',
    [string]$ExpectedVersion,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GitHub.ps1"
$identity = & "$PSScriptRoot/Build-Identity.ps1" -Apk $Apk -Aapt $Aapt -TranslationsRoot $TranslationsRoot -ExpectedGame $ExpectedGame
if ($ExpectedVersion -and $ExpectedVersion -ne $identity.sourceVersion) { throw 'Source Release tag version does not match APK versionCode.' }
$needed = $true
$release = @(Get-RepositoryReleases $Repository | Where-Object { $_.tag_name -ceq $identity.releaseTag -and !$_.draft })
if (!$Force -and $release.Count -eq 1) {
    $names = @($release[0].assets | Where-Object { $_.size -gt 0 } | ForEach-Object name)
    if ('build-info.json' -in $names -and 'SHA256SUMS.txt' -in $names -and 'identity.json' -in $names -and
        @($names | Where-Object { $_ -like '*.apk' }).Count -eq 1 -and
        @($names | Where-Object { $_ -like '*-mod-files.zip' }).Count -eq 1) {
        $temp = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.json')
        try {
            & "$PSScriptRoot/Get-ReleaseAsset.ps1" -Repository $Repository -Tag $identity.releaseTag -AssetName 'build-info.json' -Output $temp
            $previous = Get-Content $temp -Raw | ConvertFrom-Json
            $needed = $previous.fingerprint -cne $identity.fingerprint
        } finally { if (Test-Path $temp) { Remove-Item -LiteralPath $temp -Force } }
    }
}
if ($env:GITHUB_OUTPUT) {
    "needed=$needed".ToLowerInvariant() | Add-Content $env:GITHUB_OUTPUT
    "game=$($identity.game)" | Add-Content $env:GITHUB_OUTPUT
    "tag=$($identity.releaseTag)" | Add-Content $env:GITHUB_OUTPUT
}
Write-Host "$($identity.releaseTag): build needed = $needed"


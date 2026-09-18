param(
    [Parameter(Mandatory)][string]$Repository,
    [string]$SourceTag,
    [string]$AssetName
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GitHub.ps1"
$selected = @()
if ($SourceTag) {
    $entry = @{ sourceTag=$SourceTag; assetName=$AssetName; game='auto'; sourceVersion='' }
    $targets = (Get-Content "$PSScriptRoot/targets.json" -Raw | ConvertFrom-Json).targets
    foreach ($target in $targets) {
        if ($SourceTag -match ('^apk-' + [regex]::Escape($target.game) + '-v([0-9]+)$')) {
            $entry.game = $target.game
            $entry.sourceVersion = $Matches[1]
        }
    }
    $selected += $entry
} else {
    $releases = @(Get-RepositoryReleases $Repository | Where-Object { !$_.draft })
    $targets = (Get-Content "$PSScriptRoot/targets.json" -Raw | ConvertFrom-Json).targets
    foreach ($target in $targets) {
        $pattern = '^apk-' + [regex]::Escape($target.game) + '-v([0-9]+)$'
        $candidates = @($releases | Where-Object { $_.tag_name -match $pattern } | Sort-Object @{
            Expression = { [long]([regex]::Match($_.tag_name,$pattern).Groups[1].Value) }; Descending=$true
        })
        if ($candidates.Count) {
            $release = $candidates[0]
            $version = [regex]::Match($release.tag_name,$pattern).Groups[1].Value
            $selected += @{ sourceTag=$release.tag_name; assetName=''; game=$target.game; sourceVersion=$version }
        }
    }
    # Preserve repositories created with the previous apk-174 naming convention.
    if (!$selected.Count) {
        $legacy = @($releases | Where-Object { $_.tag_name.StartsWith('apk-') } | Sort-Object published_at -Descending)
        if ($legacy.Count) { $selected += @{ sourceTag=$legacy[0].tag_name; assetName=''; game='auto'; sourceVersion='' } }
    }
}
$result = @{ include=@($selected) } | ConvertTo-Json -Depth 5 -Compress
if ($env:GITHUB_OUTPUT) {
    "matrix=$result" | Add-Content $env:GITHUB_OUTPUT
    "has_targets=$($selected.Count -gt 0)".ToLowerInvariant() | Add-Content $env:GITHUB_OUTPUT
}
Write-Output $result

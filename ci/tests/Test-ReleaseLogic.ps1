$ErrorActionPreference = 'Stop'
$ci = Split-Path $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('mod-ci-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null
$savedOutput = $env:GITHUB_OUTPUT
$savedNativeExitCode = $global:LASTEXITCODE
$env:GITHUB_OUTPUT = $null
$global:ModCiTest_releases = @()
$global:ModCiTest_calls = [Collections.Generic.List[string]]::new()
$global:ModCiTest_failApi = $false
$global:ModCiTest_failUpload = $false
$global:ModCiTest_previous = @{}
function gh {
    $arguments = @($args)
    $global:ModCiTest_calls.Add(($arguments -join ' '))
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'api') {
        if ($global:ModCiTest_failApi) { $global:LASTEXITCODE = 1; return }
        if ($arguments -contains 'DELETE') { return }
        if ($arguments -contains '--slurp') {
            return ConvertTo-Json -InputObject @(,@($global:ModCiTest_releases)) -Depth 10 -Compress
        }
        return $global:ModCiTest_releases[0] | ConvertTo-Json -Depth 10 -Compress
    }
    if ($arguments[1] -eq 'download') {
        $dir = $arguments[[Array]::IndexOf($arguments,'--dir') + 1]
        $global:ModCiTest_previous | ConvertTo-Json | Set-Content (Join-Path $dir 'build-info.json')
    }
    if ($arguments[1] -eq 'upload' -and $global:ModCiTest_failUpload) { $global:LASTEXITCODE = 1 }
}
function FakeAapt {
    $global:LASTEXITCODE = 0
    "package: name='com.dmm.dmmgames.monmusutd' versionCode='174' versionName='1.1.143'"
}
function Assert($condition,[string]$message) {
    if (!$condition) { throw "FAIL: $message" }
    Write-Host "PASS: $message"
}
try {
    $global:ModCiTest_releases = @(
        @{tag_name='apk-monmusutd-v9';draft=$false;published_at='2026-01-01'},
        @{tag_name='apk-monmusutd-v174';draft=$false;published_at='2026-01-02'},
        @{tag_name='apk-monmusutdx-v180';draft=$false;published_at='2026-01-03'},
        @{tag_name='apk-monmusutdx-v999';draft=$true;published_at='2026-01-04'}
    )
    $matrix = & "$ci/Select-Targets.ps1" -Repository owner/repo | ConvertFrom-Json
    Assert ($matrix.include.Count -eq 2) 'one latest source per game'
    Assert ('apk-monmusutd-v174' -in $matrix.include.sourceTag) 'numeric version selection (174 > 9)'
    Assert ('apk-monmusutdx-v180' -in $matrix.include.sourceTag) 'draft inputs excluded'
    $manual = & "$ci/Select-Targets.ps1" -Repository owner/repo -SourceTag apk-monmusutdx-v180 | ConvertFrom-Json
    Assert ($manual.include[0].game -eq 'monmusutdx' -and $manual.include[0].sourceVersion -eq '180') 'explicit source enforces game and version'
    $global:ModCiTest_releases = @()
    $empty = & "$ci/Select-Targets.ps1" -Repository owner/repo | ConvertFrom-Json
    Assert ($empty.include.Count -eq 0) 'empty repository is a no-op'
    $global:ModCiTest_failApi = $true
    $failed = $false
    try { & "$ci/Select-Targets.ps1" -Repository owner/repo | Out-Null } catch { $failed = $true }
    Assert $failed 'API failure is not mistaken for no releases'
    $global:ModCiTest_failApi = $false
    New-Item -ItemType Directory "$temp/translations" | Out-Null
    '{}' | Set-Content "$temp/translations/manifest.json"
    'apk one' | Set-Content "$temp/input.apk"
    $identity = & "$ci/Build-Identity.ps1" -Apk "$temp/input.apk" -Aapt FakeAapt -TranslationsRoot $temp
    Assert ($identity.releaseTag -eq 'mod-monmusutd-v174') 'stable game/source version release tag'
    'apk two' | Set-Content "$temp/input.apk"
    $changed = & "$ci/Build-Identity.ps1" -Apk "$temp/input.apk" -Aapt FakeAapt -TranslationsRoot $temp
    Assert ($identity.fingerprint -ne $changed.fingerprint) 'APK replacement changes fingerprint at same version'
    '{"files":{}}' | Set-Content "$temp/translations/manifest.json"
    $translationChanged = & "$ci/Build-Identity.ps1" -Apk "$temp/input.apk" -Aapt FakeAapt -TranslationsRoot $temp
    Assert ($changed.fingerprint -ne $translationChanged.fingerprint) 'translation manifest changes fingerprint'
    $assets = @('build-info.json','SHA256SUMS.txt','identity.json','game.apk','game-mod-files.zip') | ForEach-Object { @{name=$_;size=1;id=10} }
    $global:ModCiTest_releases = @(@{tag_name=$translationChanged.releaseTag;draft=$false;assets=$assets})
    $global:ModCiTest_previous = @{fingerprint=$translationChanged.fingerprint}
    $env:GITHUB_OUTPUT = "$temp/outputs.txt"
    & "$ci/Check-Build.ps1" -Repository owner/repo -Apk "$temp/input.apk" -Aapt FakeAapt -TranslationsRoot $temp
    Assert ((Get-Content $env:GITHUB_OUTPUT) -contains 'needed=false') 'unchanged completed release is skipped'
    Clear-Content $env:GITHUB_OUTPUT
    & "$ci/Check-Build.ps1" -Repository owner/repo -Apk "$temp/input.apk" -Aapt FakeAapt -TranslationsRoot $temp -Force
    Assert ((Get-Content $env:GITHUB_OUTPUT) -contains 'needed=true') 'force rebuild overrides fingerprint'
    $global:ModCiTest_releases[0].draft = $true
    Clear-Content $env:GITHUB_OUTPUT
    & "$ci/Check-Build.ps1" -Repository owner/repo -Apk "$temp/input.apk" -Aapt FakeAapt -TranslationsRoot $temp
    Assert ((Get-Content $env:GITHUB_OUTPUT) -contains 'needed=true') 'interrupted draft must rebuild'
    New-Item -ItemType Directory "$temp/dist" | Out-Null
    @{releaseTag='mod-monmusutd-v174';game='monmusutd';sourceVersion='174';sourceCommit='test';modVersion='1'} |
        ConvertTo-Json | Set-Content "$temp/dist/build-info.json"
    'apk' | Set-Content "$temp/dist/game.apk"
    $global:ModCiTest_calls.Clear()
    & "$ci/Publish-Release.ps1" -Repository owner/repo -Output "$temp/dist"
    Assert (@($global:ModCiTest_calls | Where-Object { $_ -match 'release create' }).Count -eq 0) 'existing stable tag is updated, not recreated'
    Assert ($global:ModCiTest_calls[$global:ModCiTest_calls.Count - 1] -match '--draft=false') 'publish happens after uploads'
    $global:ModCiTest_calls.Clear()
    $global:ModCiTest_failUpload = $true
    $failed = $false
    try { & "$ci/Publish-Release.ps1" -Repository owner/repo -Output "$temp/dist" } catch { $failed = $true }
    Assert ($failed -and @($global:ModCiTest_calls | Where-Object { $_ -match '--draft=false' }).Count -eq 0) 'failed upload cannot publish partial release'
} finally {
    $env:GITHUB_OUTPUT = $savedOutput
    Remove-Item -LiteralPath $temp -Recurse -Force
    Remove-Variable -Scope Global -Name 'ModCiTest_*'
    $global:LASTEXITCODE = $savedNativeExitCode
}

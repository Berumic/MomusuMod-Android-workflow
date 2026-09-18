param(
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$Toolchain,
    [Parameter(Mandatory)][string]$TranslationsRoot,
    [Parameter(Mandatory)][string]$Keystore,
    [Parameter(Mandatory)][string]$KeyAlias,
    [string]$Java = 'java',
    [string]$Workspace = (Join-Path (Split-Path $PSScriptRoot) 'work/build'),
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot) 'dist')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$config = Get-Content "$PSScriptRoot/toolchain.json" -Raw | ConvertFrom-Json
function Check-Exit([string]$Message) { if ($LASTEXITCODE -ne 0) { throw $Message } }
if (!$env:APK_STORE_PASSWORD -or !$env:APK_KEY_PASSWORD) { throw 'Signing passwords are missing.' }
if (Test-Path $Workspace) { throw 'Build workspace must be new; choose a fresh -Workspace.' }
if ((Test-Path $Output) -and @(Get-ChildItem $Output -Force).Count) { throw 'Output directory must be empty.' }
New-Item -ItemType Directory -Force $Workspace,$Output | Out-Null
# Windows aapt cannot reliably open non-ASCII APK filenames.
Copy-Item -LiteralPath $Apk -Destination "$Workspace/input.apk"
$Apk = Join-Path $Workspace 'input.apk'
$lemon = Join-Path $Toolchain 'Lemon'
$sdk = Join-Path $Toolchain 'BuildTools'
$badging = & "$sdk/aapt.exe" dump badging $Apk
Check-Exit 'Cannot read APK metadata.'
$packageLine = ($badging | Where-Object { $_ -match '^package:' }) -join ''
if ($packageLine -notmatch "^package: name='([^']+)' versionCode='([0-9]+)' versionName='([^']*)'") { throw 'Invalid APK package metadata.' }
$package = $Matches[1]; $code = $Matches[2]; $version = $Matches[3]
if ($package -ne $config.packageName) { throw "Wrong package: $package" }
$zip = [IO.Compression.ZipFile]::OpenRead($Apk)
try {
    foreach ($path in @('lib/arm64-v8a/libil2cpp.so','assets/bin/Data/Managed/Metadata/global-metadata.dat','assets/bin/Data/globalgamemanagers')) {
        if (!$zip.GetEntry($path)) { throw "Not a supported complete ARM64 APK: missing $path" }
    }
    if ($zip.GetEntry('assets/LemonLoader/deployment/Mods/MonsterMusumeTDMod.Android.dll')) { throw 'Upload the original APK, not an already patched APK.' }
    $stream = $zip.GetEntry('assets/bin/Data/globalgamemanagers').Open()
    try {
        $buffer = [byte[]]::new(256)
        $count = $stream.Read($buffer,0,$buffer.Length)
        $header = [Text.Encoding]::ASCII.GetString($buffer,0,$count)
        if ($header -notmatch '\d{4}\.\d+\.\d+[abfp]\d+') { throw 'Cannot detect Unity version.' }
        $unity = $Matches[0]
        if ($unity -ne $config.unityVersion) { throw "Unity $unity requires a matching toolchain (configured: $($config.unityVersion))." }
    } finally { $stream.Dispose() }
} finally { $zip.Dispose() }
& "$root/Android/Prepare.ps1" -LemonRoot $lemon -Workspace $Workspace -Apk $Apk
& "$root/Android/Build.ps1" -Workspace $Workspace -Translations "$TranslationsRoot/translations" -FontBundle "$Toolchain/Font/alimama-android"
& dotnet run --project "$root/Android/Tests/Android.Tests.csproj" -c Release "-p:AndroidWorkspace=$Workspace" -- $Workspace
Check-Exit 'Android regression tests failed.'
$deployments = @(Get-ChildItem $Workspace -Directory -Filter 'Deployment-*')
if ($deployments.Count -ne 1) { throw 'Expected one deployment.' }
$deployment = $deployments[0].FullName
$unsigned = "$Workspace/unsigned.apk"
& "$lemon/CLI/LemonLoader.Patcher.CLI.exe" patch $Apk --output $unsigned `
    --release "$lemon/.tools/LemonLoader-Android-arm64.zip" `
    --deployment $deployment --profile development `
    --policy 'Mods/MonsterMusumeTDMod.Android.dll=refresh' `
    --policy 'UserData/alimama-android=upgrade' `
    --policy 'UserData/MonsterMusumeTDMod/assets-manifest.json=upgrade' `
    --unity-libraries "$lemon/.tools/UnityDependencies/2022.3.62" `
    --cpp2il "$lemon/.tools/Cpp2IL-2022.1.0-pre-release.21-Windows.exe" `
    --il2cppinterop-cli "$lemon/Tools/Il2CppInterop/Il2CppInterop.CLI.dll"
Check-Exit 'LemonLoader packaging failed.'
[xml]$project = Get-Content "$root/Android/MonsterMusumeTDMod.Android.csproj"
$modVersion = $project.Project.PropertyGroup.Version
$name = "MonsterMusumeTD-$code-Mod-$modVersion"
$signed = "$Output/$name.apk"
& "$sdk/zipalign.exe" -f -P 16 4 $unsigned "$Workspace/aligned.apk"
Check-Exit 'APK alignment failed.'
& $Java -jar "$sdk/lib/apksigner.jar" sign --ks $Keystore --ks-key-alias $KeyAlias `
    --ks-pass env:APK_STORE_PASSWORD --key-pass env:APK_KEY_PASSWORD --out $signed "$Workspace/aligned.apk"
Check-Exit 'APK signing failed.'
& $Java -jar "$sdk/lib/apksigner.jar" verify --verbose --print-certs $signed
Check-Exit 'APK signature verification failed.'
& "$sdk/zipalign.exe" -c -P 16 4 $signed
Check-Exit 'Signed APK alignment verification failed.'
$zip = [IO.Compression.ZipFile]::OpenRead($signed)
try {
    foreach ($file in Get-ChildItem $deployment -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($deployment,$file.FullName).Replace('\','/')
        if ($relative -eq 'README.md') { continue }
        $entry = $zip.GetEntry("assets/LemonLoader/deployment/$relative")
        if (!$entry) { throw "Missing APK payload: $relative" }
        $stream = $entry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [Convert]::ToHexString($hasher.ComputeHash($stream)) }
        finally { $hasher.Dispose(); $stream.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "APK payload mismatch: $relative" }
    }
} finally { $zip.Dispose() }
Copy-Item "$deployment.zip" "$Output/$name-mod-files.zip"
$translationCommit = & git -C $TranslationsRoot rev-parse HEAD
Check-Exit 'Cannot identify translation commit.'
$sourceCommit = 'local-uncommitted'
if (Test-Path "$root/.git") {
    $revision = & git -C $root rev-parse --quiet --verify HEAD
    if ($LASTEXITCODE -eq 0) { $sourceCommit = "$revision".Trim() }
}
if ($env:GITHUB_ACTIONS -eq 'true' -and $sourceCommit -eq 'local-uncommitted') { throw 'Cannot identify Mod source commit.' }
$metadata = [ordered]@{
    packageName=$package; versionCode=$code; versionName=$version; unityVersion=$unity
    modVersion=$modVersion; sourceApkSha256=(Get-FileHash $Apk).Hash.ToLowerInvariant()
    signedApkSha256=(Get-FileHash $signed).Hash.ToLowerInvariant()
    translationRepository='Berumic/MonsterMusumeTDChineseTranslation'; translationCommit="$translationCommit".Trim()
    toolchainSha256=$config.sha256; sourceCommit="$sourceCommit".Trim()
    runId=$env:GITHUB_RUN_ID; generatedAt=[DateTime]::UtcNow.ToString('o')
}
$metadata | ConvertTo-Json | Set-Content "$Output/build-info.json" -Encoding utf8
Get-ChildItem $Output -File | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content "$Output/SHA256SUMS.txt" -Encoding utf8
Write-Host "Verified signed APK: $signed"

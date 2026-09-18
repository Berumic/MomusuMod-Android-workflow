param(
    [Parameter(Mandatory)][string]$LemonRoot,
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$Apk
)
$ErrorActionPreference = 'Stop'
if (!$Apk) {
    $apks = @(Get-ChildItem -LiteralPath $LemonRoot -Filter '*.apk')
    if ($apks.Count -ne 1) { throw 'Specify -Apk when the LemonLoader directory does not contain exactly one APK.' }
    $Apk = $apks[0].FullName
}
$release = Join-Path $LemonRoot '.tools\LemonLoader-Android-arm64.zip'
$loader = Join-Path $Workspace 'Loader'
New-Item -ItemType Directory -Force -Path $loader | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($release)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -like 'assets/LemonLoader/runtime/loader/net6/*' -and $entry.Name) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $loader $entry.Name), $true)
        }
    }
} finally { $zip.Dispose() }
& "$LemonRoot\CLI\LemonLoader.Patcher.CLI.exe" patch $Apk `
    --output "$Workspace\MonsterMusumeTD-lemon-base.apk" --release $release `
    --unity-libraries "$LemonRoot\.tools\UnityDependencies\2022.3.62" `
    --interop-output "$Workspace\Interop" `
    --cpp2il "$LemonRoot\.tools\Cpp2IL-2022.1.0-pre-release.21-Windows.exe" `
    --il2cppinterop-cli "$LemonRoot\Tools\Il2CppInterop\Il2CppInterop.CLI.dll"
if ($LASTEXITCODE -ne 0) { throw 'LemonLoader preparation failed.' }

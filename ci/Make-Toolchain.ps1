param(
    [string]$LemonRoot = 'D:\mms\LemonLoader',
    [string]$AndroidRoot = 'D:\mms\MomusuMod-Android'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$stage = Join-Path $root ('work/toolchain-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$stage/Lemon/.tools","$stage/Font","$root/release-assets" | Out-Null
Copy-Item "$LemonRoot/CLI" "$stage/Lemon/CLI" -Recurse
Copy-Item "$LemonRoot/Tools" "$stage/Lemon/Tools" -Recurse
Copy-Item "$LemonRoot/LICENSE","$LemonRoot/NOTICE" "$stage/Lemon"
Copy-Item "$LemonRoot/.tools/LemonLoader-Android-arm64.zip","$LemonRoot/.tools/Cpp2IL-2022.1.0-pre-release.21-Windows.exe" "$stage/Lemon/.tools"
Copy-Item "$LemonRoot/.tools/UnityDependencies" "$stage/Lemon/.tools/UnityDependencies" -Recurse
Copy-Item "$AndroidRoot/FontBundle/alimama-android" "$stage/Font"
Copy-Item "$AndroidRoot/SigningTools/build-tools-35/android-15" "$stage/BuildTools" -Recurse
$out = "$root/release-assets/android-toolchain-v1.zip"
if (Test-Path $out) { throw "Already exists: $out. Keep the pinned archive or move it before rebuilding." }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $out)
Write-Host "Toolchain: $out"
Write-Host "SHA256: $((Get-FileHash $out).Hash.ToLowerInvariant())"


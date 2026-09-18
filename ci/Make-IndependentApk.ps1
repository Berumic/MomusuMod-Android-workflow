param(
    [Parameter(Mandatory)][string]$InputApk,
    [Parameter(Mandatory)][string]$OutputApk,
    [Parameter(Mandatory)][string]$Apktool,
    [Parameter(Mandatory)][string]$SourcePackage,
    [Parameter(Mandatory)][string]$ModPackage,
    [Parameter(Mandatory)][string]$Label,
    [Parameter(Mandatory)][string]$Workspace,
    [string]$Java = 'java'
)
$ErrorActionPreference = 'Stop'
$config = Get-Content "$PSScriptRoot/toolchain.json" -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $Apktool).Hash -ne $config.apktool.sha256) { throw 'Apktool checksum mismatch.' }
$decoded = Join-Path $Workspace 'clone-decoded'
& $Java -jar $Apktool d --frame-path "$Workspace/framework" -o $decoded $InputApk
if ($LASTEXITCODE -ne 0) { throw 'Apktool decode failed.' }
& python "$PSScriptRoot/Rewrite-Manifest.py" --directory $decoded --source-package $SourcePackage --mod-package $ModPackage --label $Label --report "$Workspace/identity.json"
if ($LASTEXITCODE -ne 0) { throw 'Independent package rewrite failed.' }
& $Java -jar $Apktool b --frame-path "$Workspace/framework" -o $OutputApk $decoded
if ($LASTEXITCODE -ne 0) { throw 'Independent package rebuild failed.' }


param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Keystore,
    [Parameter(Mandatory)][string]$KeyAlias
)
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Use OWNER/REPOSITORY.' }
if (!(Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Install GitHub CLI and run gh auth login first.' }
& gh auth status
if ($LASTEXITCODE -ne 0) { throw 'Run gh auth login first.' }
$store = Read-Host 'Keystore password' -AsSecureString
$key = Read-Host 'Key password (enter the same value when identical)' -AsSecureString
function Set-Secret([string]$Name,[string]$Value) {
    $Value | & gh secret set $Name --repo $Repository
    if ($LASTEXITCODE -ne 0) { throw "Failed to set $Name." }
}
try {
    Set-Secret 'APK_KEYSTORE_BASE64' ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $Keystore))))
    Set-Secret 'APK_KEY_ALIAS' $KeyAlias
    Set-Secret 'APK_STORE_PASSWORD' ([Net.NetworkCredential]::new('', $store).Password)
    Set-Secret 'APK_KEY_PASSWORD' ([Net.NetworkCredential]::new('', $key).Password)
} finally {
    $store.Dispose()
    $key.Dispose()
}
Write-Host 'Four repository signing secrets configured.'


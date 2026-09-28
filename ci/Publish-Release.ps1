param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GitHub.ps1"
$info = Get-Content "$Output/build-info.json" -Raw | ConvertFrom-Json
$files = @(Get-ChildItem $Output -File -Filter '*.apk')
if ($files.Count -ne 1 -or $files[0].Length -le 0) { throw 'Expected exactly one nonempty final APK.' }
if ($info.fingerprint -notmatch '^[0-9a-f]{64}$') { throw 'Missing or invalid build fingerprint.' }
$tag = $info.releaseTag
if ($tag -cne "mod-$($info.game)-v$($info.sourceVersion)" -or $tag -notmatch '^mod-[a-z0-9-]+-v[0-9]+$') { throw 'Invalid release tag.' }
$title = "$($info.game) v$($info.sourceVersion) / Mod $($info.modVersion)"
$notes = @(
    "Source versionCode: $($info.sourceVersion); display version: $($info.versionName)",
    "Independent package: $($info.packageName)",
    "Source APK Release: $($info.sourceReleaseTag)",
    "Mod commit: $($info.sourceCommit)",
    "Translations commit: $($info.translationCommit)",
    "Build fingerprint: $($info.fingerprint)",
    "APK SHA256: $((Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant())",
    "This tag is updated when APK, Mod or build recipe changes. Build metadata is recorded in these release notes; only the final APK is attached.",
    "Original and Mod use separate application data. DMM login requires device validation."
) -join "`n`n"
$notesFile = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.md')
[IO.File]::WriteAllText($notesFile,$notes)
try {
    $existing = @(Get-RepositoryReleases $Repository | Where-Object { $_.tag_name -ceq $tag })
    if ($existing.Count) {
        # Hide the release while replacing files; a failed replacement stays a draft.
        & gh release edit $tag --repo $Repository --draft=true --title $title --notes-file $notesFile
    } else {
        & gh release create $tag --repo $Repository --target $info.sourceCommit --title $title --notes-file $notesFile --draft --latest=false
    }
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare draft release.' }
    & gh release upload $tag $files[0].FullName --repo $Repository --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Release upload failed; draft retained.' }
    $keep = @($files.Name)
    if ($existing.Count) {
        foreach ($asset in $existing[0].assets) {
            if ($asset.name -notin $keep) {
                & gh api --method DELETE "repos/$Repository/releases/assets/$($asset.id)"
                if ($LASTEXITCODE -ne 0) { throw 'Cannot remove obsolete release asset; draft retained.' }
            }
        }
    }
    # A non-draft release with the notes fingerprint is the completion marker.
    # Mark the generated Mod release as the repository's Latest release.
    & gh release edit $tag --repo $Repository --draft=false --latest=true
    if ($LASTEXITCODE -ne 0) { throw 'Cannot publish completed release; draft retained.' }
} finally { Remove-Item -LiteralPath $notesFile -Force }

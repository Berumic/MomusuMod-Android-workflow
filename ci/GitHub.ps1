function Get-RepositoryReleases([string]$Repository) {
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
    $json = & gh api --paginate --slurp "repos/$Repository/releases?per_page=100"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list releases (check token permissions/network).' }
    $pages = ($json -join "`n") | ConvertFrom-Json
    foreach ($page in $pages) { foreach ($release in $page) { $release } }
}


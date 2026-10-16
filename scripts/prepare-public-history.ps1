[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Git {
    $Arguments = $args
    $output = & git.exe --no-replace-objects -c "safe.directory=$root" -C $root @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments -join ' ')" }
    $output
}
$origin = Git remote get-url origin
if ($origin -notmatch '^(https://github\.com/Refilewski/Azimuth-RPC(?:\.git)?|git@github\.com:Refilewski/Azimuth-RPC(?:\.git)?)$') {
    throw 'Expected origin Refilewski/Azimuth-RPC; no history changes made'
}
if (@(Git status --porcelain).Count) { throw 'Working tree/index must be clean, including untracked files' }
foreach ($required in @('README.md','CONTRIBUTING.md','docs/PUBLICATION_AUDIT.md','docs/PUBLIC_RELEASE.md',
    'tests/fixtures/synthetic/scenarios.json','tests/SyntheticFixtures.cs','src/AzimuthRPC.Core/Game/GameSnapshotReader.cs',
    'native/FH2ML.AzimuthRPC/AzimuthRpcMod.cpp','.github/workflows/build-pr.yml')) {
    Git cat-file -e "HEAD:$required" | Out-Null
}
& (Join-Path $PSScriptRoot 'audit-public-tree.ps1') -RepositoryRoot $root
$audit = [IO.File]::ReadAllText((Join-Path $root 'docs/PUBLICATION_AUDIT.md'))
$ready = $audit -match '(?m)^HISTORY PREPARATION STATUS: READY\s*$' -and
    $audit -notmatch '(?m)^HISTORY PREPARATION STATUS: BLOCKED\s*$' -and
    (Test-Path -LiteralPath (Join-Path $root 'LICENSE') -PathType Leaf)
if ($ready) {
    Git cat-file -e 'HEAD:LICENSE' | Out-Null
    $ready = (Get-Item -LiteralPath (Join-Path $root 'LICENSE')).Length -gt 0
}
Write-Output 'Current branches:'
Git branch -avv
Write-Output 'Current tags:'
Git tag --list
Write-Output 'Old remote branches/tags, PR refs and Releases may retain private history. An orphan branch alone does not hide it.'
Write-Output 'Plan: create ONLY local public-main with the current HEAD tree and one parentless commit: Initial public release.'
Write-Output 'No branch checkout, remote update, deletion, release or visibility change will be performed.'
if (-not $ready) {
    Write-Output 'BLOCKED: local history-preparation/license prerequisites are unresolved. Dry run remains read-only; -Apply is refused.'
    if ($Apply) { throw 'Record history-preparation approval and commit a selected license before applying' }
    return
}
if (-not $Apply) { Write-Output 'DRY RUN complete. Review the audit and rerun with -Apply when approved.'; return }
$existing = & git.exe --no-replace-objects -c "safe.directory=$root" -C $root show-ref --verify --quiet refs/heads/public-main
if ($LASTEXITCODE -eq 0) { throw 'Local public-main already exists; refusing replacement' }
if ($LASTEXITCODE -ne 1) { throw 'Cannot determine whether public-main exists' }
$tree = Git rev-parse 'HEAD^{tree}'
# commit-tree without -p creates a parentless commit and leaves checkout/index intact.
$commit = Git -c core.hooksPath=NUL commit-tree $tree -m 'Initial public release'
Git update-ref refs/heads/public-main $commit ('0' * 40)
if ((Git rev-list --count public-main) -ne '1' -or (Git rev-parse 'public-main^{tree}') -ne $tree) {
    throw 'Root/tree verification failed; inspect local public-main. Nothing was pushed.'
}
Write-Output "Created local public-main: $commit (one root commit; checkout unchanged)."
Write-Output 'Local history preparation does not clear remote history, resolve provenance or authorize a public visibility change.'
Write-Output 'Next manual steps: git show --stat public-main; git switch public-main; ./publish.ps1; ./native/FH2ML.AzimuthRPC/build.ps1'
Write-Output 'After review: git push origin public-main; then review docs/PUBLIC_RELEASE.md before creating main or changing defaults.'

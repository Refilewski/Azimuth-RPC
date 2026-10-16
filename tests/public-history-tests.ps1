$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sandbox = Join-Path $repo ("artifacts/history-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
function Git {
    $Arguments = $args
    $output = & git.exe -c "safe.directory=$sandbox" -C $sandbox @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Test git failed: $($Arguments -join ' ')" }
    $output
}
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Reject([scriptblock]$action) {
    $rejected = $false
    try { & $action | Out-Null } catch { $rejected = $true }
    Check $rejected 'Expected operation to be refused'
}
Git init -q
Git config user.name 'Synthetic Test'
Git config user.email 'test@example.invalid'
Git remote add origin https://github.com/Refilewski/Azimuth-RPC.git
$files = @('README.md','CONTRIBUTING.md','docs/PUBLICATION_AUDIT.md','docs/PUBLIC_RELEASE.md',
    'tests/fixtures/synthetic/scenarios.json','tests/SyntheticFixtures.cs','src/AzimuthRPC.Core/Game/GameSnapshotReader.cs',
    'native/FH2ML.AzimuthRPC/AzimuthRpcMod.cpp','.github/workflows/build-pr.yml')
foreach ($file in $files) {
    $path = Join-Path $sandbox $file
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    [IO.File]::WriteAllText($path, 'Synthetic test placeholder')
}
$scripts = Join-Path $sandbox 'scripts'
New-Item -ItemType Directory -Force -Path $scripts | Out-Null
Copy-Item (Join-Path $repo 'scripts/*.ps1') $scripts
$auditPath = Join-Path $sandbox 'docs/PUBLICATION_AUDIT.md'
[IO.File]::WriteAllText($auditPath, "HISTORY PREPARATION STATUS: BLOCKED`nPUBLIC RELEASE STATUS: BLOCKED`n")
Git add .
Git commit -qm 'Synthetic private history'
$script = Join-Path $scripts 'prepare-public-history.ps1'
$before = Git rev-parse HEAD
& $script | Out-Null
Reject { & $script -Apply }
Check ((Git rev-parse HEAD) -eq $before) 'Blocked apply changed HEAD'
Git remote set-url origin https://github.com/example/wrong-repository.git
Reject { & $script }
Git remote set-url origin https://github.com/Refilewski/Azimuth-RPC.git
[IO.File]::WriteAllText((Join-Path $sandbox 'untracked.txt'), 'Synthetic dirty file')
Reject { & $script }
Remove-Item -LiteralPath (Join-Path $sandbox 'untracked.txt')
$raw = Join-Path $sandbox 'tests/fixtures/live/raw.json'
New-Item -ItemType Directory -Force -Path (Split-Path $raw -Parent) | Out-Null
[IO.File]::WriteAllText($raw, '{}')
Git add tests/fixtures/live/raw.json
Git commit -qm 'Synthetic forbidden tracked fixture'
Reject { & $script }
Git rm -q tests/fixtures/live/raw.json
[IO.File]::WriteAllText($auditPath, "HISTORY PREPARATION STATUS: READY`nPUBLIC RELEASE STATUS: BLOCKED`n")
[IO.File]::WriteAllText((Join-Path $sandbox 'LICENSE'), 'Synthetic test placeholder; not a selected project license')
Git add docs/PUBLICATION_AUDIT.md LICENSE
Git commit -qm 'Synthetic cleared fixture'
$before = Git rev-parse HEAD
$tree = Git rev-parse 'HEAD^{tree}'
& $script | Out-Null
Check (@(Git for-each-ref '--format=%(refname)' refs/heads/public-main).Count -eq 0) 'Dry run created a ref'
& $script -Apply | Out-Null
Check ((Git rev-list --count public-main) -eq '1') 'Public history has parents'
Check ((Git log -1 --format=%s public-main) -eq 'Initial public release') 'Wrong public commit message'
Check ((Git rev-parse 'public-main^{tree}') -eq $tree) 'Public tree changed'
Check ((Git rev-parse HEAD) -eq $before) 'Apply changed current checkout'
Reject { & $script -Apply }
Write-Output 'PASS history tooling: blocked audit, wrong origin, dirty tree, raw fixture, read-only dry run, one parentless identical-tree commit, unchanged checkout, duplicate refusal.'

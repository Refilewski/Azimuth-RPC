[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$files = @(& git.exe -c "safe.directory=$root" -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked repository files' }
$issues = [Collections.Generic.List[string]]::new()
$approvedBinaryFiles = @(
    'src/AzimuthRPC.App/Assets/AzimuthRPC.ico',
    'src/AzimuthRPC.Debug/Assets/AzimuthRPC.ico'
)
foreach ($file in $files) {
    if ($file -match '(^|/)(bin|obj|build|artifacts|dumps|\.vs)/|^tests/fixtures/live/|^research/|(^|/)rpc-config\.json$|\.(log|pdb|exe|dll|pfx|p12|key|user|suo)$|(^|/)\.env($|\.)') {
        $issues.Add("Forbidden tracked file: $file")
    }
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $issues.Add("Missing tracked file: $file"); continue }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -gt 1MB) { $issues.Add("Oversized tracked file: $file") }
    if ($bytes -contains 0) {
        if ($approvedBinaryFiles -notcontains $file) {
            $issues.Add("Binary file requires explicit publication review: $file")
        }
        continue
    }
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    # Report only filenames, never matched credentials or private values.
    if ($text -match '(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY)') {
        $issues.Add("Possible credential/private key: $file")
    }
    $userPathPattern = '(?i)[a-z]:[\\/]+' + 'users' + '[\\/]+[^\\/\s"'']+|/' + 'Users' + '/[^/\s"'']+'
    if ($text -match $userPathPattern) {
        $issues.Add("Machine-specific user path: $file")
    }
    if ($text -match '[A-Fa-f0-9]{257,}' -or $text -match '"Hex"\s*:') {
        $issues.Add("Raw/large memory data: $file")
    }
}
if ($issues.Count) { throw ($issues -join "`n") }
Write-Output "Public-tree structural scan passed ($($files.Count) files). This does not certify authorship or licensing."

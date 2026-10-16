# Publication audit — 2026-10-16

Repository: Refilewski/Azimuth-RPC. Base: archived-privately
(`refactor/codex-cleanup`, 0.9.1). Preparation branch: chore/public-open-source-prep.
Visibility remains private. No default-branch rewrite, remote deletion or Release
publication is performed by this preparation.

NOT READY FOR PUBLIC RELEASE

HISTORY PREPARATION STATUS: READY

## Findings and disposition

| Area | Classification | Result / required action |
|---|---|---|
| tests/fixtures/live and research/v6.5-live-captures | Remove/sanitize | 22 raw captures plus label manifest removed; replaced with 22 scalar synthetic scenarios and a memory builder |
| Generated research JSON/CSV/reports | Remove from public tree | Removed remaining research/ output; no runtime/test depends on it |
| docs/research/history | Remove/sanitize | Internal task and refactor reports removed. Meaningful corrections retained in docs/research/STATE_FINDINGS.md and OFFSETS.md |
| src/, native/, tools/research | Useful code; provenance review | Runtime, debugging and bounded read-only research tools retained; specific ownership questions below |
| docs/ARCHITECTURE, OFFSETS, RESEARCH and MAP-RESEARCH | Safe with confidence limits | Current layouts, failed hypotheses and unresolved native location work retained; raw-fixture guidance updated |
| config/, JSON/YAML/PowerShell/csproj/CMake | Safe after review | Public example only; no actual private config, credentials or machine path found |
| Source icon | Remove; origin unverified | Undocumented binary icon removed; platform/default application icon used |
| Broad CREDITS | Remove broad courtesy list | Removed. Exact unresolved attribution/provenance facts retained in PROVENANCE.md and historical discovery notes in OFFSETS.md |
| Old native generated-source patch | Replace build machinery | CMake string rewriting removed; location integration is explicit in maintainable source, behavior preserved |
| Ignored local dumps/build/logs | Keep local | Expanded ignores and repeatable tracked-tree scanner; source ZIP/releases need separate review |
| Old Git refs/Releases | Blocker for history goal | All audited refs reach private development history; manual migration and GitHub-side reachability review required |

## Fixture reduction and coverage

Before: **47,511,462 bytes** in 23 tracked files (22 captures + labels).
After: **7,387 bytes** in `tests/fixtures/synthetic/scenarios.json` (22 cases),
plus the readable test-only builder and fixture policy. No Hex fields, raw pointer
addresses or arbitrary heap surroundings remain in the JSON. The builder uses
invented heap addresses and zero-filled arrays only as large as tested reads need.
Names needed by record guards are constructed, not extracted assets.

Both suites preserve all 74 existing checks (41 UI + 33 core). Cases cover PE/build
validation, relocated GameBase, car parsing/display, speed, XYZ, velocity,
Pause/Photo/Map/Hub/Garage/Loading, activity, world presence, repeated-read failures,
changed pointer chains, IPC framing and local non-executing opcode regression.
The per-case core checks now also assert exact synthetic car/display/vector/speed
and world-presence expectations. Five historically unlabeled cases remain parser
controls, not newly asserted live ground truth. Synthetic tests do not replace
future live validation of unsupported combinations.

## Security and privacy review

The entire tracked tree was enumerated and scanned programmatically; build/obj
files were excluded by using Git's tracked-file list. Reviewed keyword hits for
password/secret/token/API key/authorization/bearer, local user paths, emails,
private-key markers, credential formats and long hex values. Cancellation tokens
and GitHub's workflow token expression are normal code, not embedded credentials.
Discord Application IDs, public repository links and NuGet endpoints are public
identifiers. No actual secret/private user path/email was found in the cleaned
tracked tree. No game executable, image, map asset or binary dependency remains.

`scripts/audit-public-tree.ps1` rejects raw fixtures, local config/logs, generated
directories, binary files, oversized data, typical credential formats, private
user paths and long raw hex. It never prints matched secrets. It is a structural
guard, not an exhaustive credential scanner or a licensing certificate. Native
logs deliberately contain probe bytes/module names; documentation and the issue
form require review/redaction before upload. Historical private commits are not
cleared by this current-tree scan.

## Implementation/provenance record and maintainer decision

No demonstrably copied implementation was identified. Local source comparison
against inspected ExtraOptions, DualSense, LivingCockpit and Horizon Online
snapshots found no identical contiguous 50-token windows after comment removal.
This limited test cannot exclude translation, structural derivation or earlier
upstream versions. No cosmetic rewrite is represented as independent authorship.

**No independent telemetry/source rewrite is claimed.** Prior review limitations
are retained below. On 2026-10-16 the maintainer instructed preparation to proceed
without further provenance work and selected the AzimuthRPC Use-Only License 1.0.
This decision allows local history preparation; it is not new authorship evidence
or a waiver of third-party rights. The recorded questions concern:

1. `src/AzimuthRPC.Debug/Native/Fh2TelemetryHook.cs` and `X64TelemetryStubs.cs`:
   establish original authorship or replace from independently observed behavior
   and x64/platform documentation. Existing tests alone do not prove permission.
2. `src/AzimuthRPC.Core/Game/`, `State/`, `Discord/`, the original debug/research
   helpers and `native/FH2ML.AzimuthRPC/AzimuthRpcMod.cpp`: maintainer must confirm
   ownership/development provenance of the supplied implementations. No upstream
   SDK implementation or NuGet PackageReference is vendored.
3. Both `Fh2MainlandLocationResolver.cs` and `LocationResolver.cpp`: confirm the
   origin/rights of hand-authored approximate polygon coordinates. Map imagery is
   absent; the resolver is a coordinate heuristic, not native region discovery.
4. The historical request for credit from Horizon Online does not identify the
   exact reused component. Resolve its scope before removing this specific note
   or declaring no remaining notice obligations. Original root/event research
   attribution to ExtraOptions is preserved as factual provenance.

These are unresolved provenance questions, not findings that the listed files
necessarily infringe. See PROVENANCE.md. No third-party permission or legal clearance is
asserted. No legally required source notice was identified and knowingly removed.
Gameplay features are retained; the only removed visual feature is the unverified
custom icon. The selected restrictive license grants only rights the licensor controls and
preserves third-party terms. See LICENSE and LICENSE_POLICY.md. This is now a
source-available publication preparation, not an open-source release.

## Branch audit (private commit IDs archived separately)

All remote branches below were checked with merge-base against the audited default
commit. “Safe later” means merged content is preserved in the clean tree or
explicitly archived privately, after CI/protection/default changes and maintainer
approval; it does not authorize deletion now.

| Branch | Purpose | Merged? | Safe to delete after migration? |
|---|---|---|---|
| chore/0.9.0-prep | Dual-runtime release preparation | Yes | Yes, after review/archive and main migration |
| chore/ci-consolidation | CI consolidation | Yes | Yes, after review/archive and main migration |
| cleanup/remove-v0.9.0-publisher | Remove one-shot publisher | Yes | Yes, after review/archive and main migration |
| cleanup/repo-hygiene | Repository/fixture hygiene | Yes | Yes, after review/archive and main migration |
| docs/current-state-cleanup | Current docs cleanup | Yes | Yes, after review/archive and main migration |
| feature/fh2ml-rpc-mod | Native FH2ML RPC runtime | Yes | Yes, after review/archive and main migration |
| feature/native-location | Location implementation/research | Yes | Yes, after review/archive and main migration |
| fix/native-diagnostics-0.9.1 | Native diagnostics 0.9.1 | Yes | Yes, after review/archive and main migration |
| refactor/codex-cleanup | Current private default | Yes | Yes, after review/archive and main migration |
| release/0.8.0 | Standalone 0.8.0 | Yes | Yes, after review/archive and main migration |
| release/0.8.1 | Tray/icon update | Yes | Yes, after review/archive and main migration |
| release/0.8.2 | Standalone release cleanup | Yes | Yes, after review/archive and main migration |
| release/v0.9.0-publish | One-shot Release publisher | Yes | Yes, after review/archive and main migration |
| release/v0.9.0-tag | One-shot tag publisher | Yes | Yes, after review/archive and main migration |
| research/map-logger | Native map research | Yes | Yes, after review/archive and main migration |

The preparation branch itself must also be reviewed/removed after migration; it retains private ancestry.

## Tags and Releases (private commit IDs omitted)

| Tag | Target commit | Old private history exposed? | Recommended action |
|---|---|---|---|
| v0.8.0 | `archived-privately` | Yes | Review/remove old tag only after approval; recreate on new history if needed |
| v0.8.1 | `archived-privately` | Yes | Review/remove old tag only after approval; recreate on new history if needed |
| v0.8.2 | `archived-privately` | Yes | Review/remove old tag only after approval; recreate on new history if needed |
| v0.9.0 | `archived-privately` | Yes | Review/remove old tag only after approval; recreate on new history if needed |
| v6.8-working | `archived-privately` | Yes | Review/remove old tag only after approval; recreate on new history if needed |

GitHub API audit found four Releases: **v0.8.0, v0.8.1, v0.8.2 and v0.9.0**.
They use the old-history tags above. v0.9.0 includes EXE, DLL and native ZIP;
the earlier three include EXE assets. Review source archives, assets, Release
notes and provenance before any public switch; later recreation from the clean
history is preferable to silently retaining old artifacts. v6.8-working is a
remote development tag without a Release in the API listing. No v0.9.1 tag or
Release was present at audit time. No tag/Release was changed.


## Validation and final manual actions

Managed Release build and both regression executables pass. Native MSVC x64 DLL
build passes with warnings treated as errors. History-tool tests cover blocked
audit, wrong origin, dirty tree, tracked raw fixtures, read-only dry run, exactly
one parentless commit with the same tree, unchanged checkout and duplicate refusal.
Full `publish.ps1` passes, including managed build, all 74 checks, tray smoke test
and self-contained EXE packaging. Final x64 artifacts: EXE 71,634,918 bytes; native
DLL 68,608 bytes with FH2ML_Initialize export; manifest/README packaged. Both YAML
files parse successfully; workflow conditions/permissions and 0.9.1 version
agreement were checked programmatically. The tracked-tree structural scan passes.
No raw dump is needed by build, tests or smoke test.

CI accepts PRs to refactor/codex-cleanup and main, defaults to contents:read and
grants contents:write only to the separately conditioned release job. Ordinary
PRs cannot publish Releases. Standalone/native versions remain 0.9.1.

Follow the exact commands/checklist in PUBLIC_RELEASE.md: review the selected
license and retained provenance record; retest; dry-run history tooling; apply
local history preparation as authorized;
inspect/build public-main; push it; create main only after checking existing refs;
change default and CI; individually review old branches/tags/Releases and PR/cache
reachability; inspect the GitHub UI; only then consider changing visibility.
An orphan main alone cannot hide history retained by other refs, PRs, cached views
or clones. No GitHub-side erasure guarantee is made.

PUBLIC RELEASE STATUS: BLOCKED

Remaining reasons: fresh remote history migration, old-ref/Release disposition
and GitHub PR/cache reachability review are pending. The maintainer selected a
restrictive use-only license and chose to proceed without further provenance work;
that choice does not resolve third-party rights or certify legal clearance.
Visibility remains private until final review.

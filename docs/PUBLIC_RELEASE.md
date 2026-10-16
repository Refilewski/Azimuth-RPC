# Manual public-release migration

Keep **Refilewski/Azimuth-RPC**. This PR changes neither visibility nor the default
branch, and performs no remote history rewrite, ref deletion or release publishing.
The repository remains **not ready for public visibility** until remote history
and PR/cache reachability have been reviewed. The maintainer selected the
restrictive AzimuthRPC Use-Only License 1.0 and authorized local history preparation.
The publication is source-available, not open source. Prior provenance limits
remain recorded; proceeding does not override third-party rights.

1. Review and merge the preparation PR; confirm both managed/native CI are green.
2. Review LICENSE and LICENSE_POLICY.md. They allow own use/builds and necessary
   copies, reserve code reuse/redist rights, and preserve third-party and GitHub
   hosting permissions. The maintainer chose to proceed without further provenance
   work; do not present that decision as legal clearance.
3. Confirm no raw fixtures, private configs, logs or game assets are tracked.
4. From a clean checkout run `./scripts/prepare-public-history.ps1`. Review the
   dry run, all branches/tags and publication blockers. It makes no changes.
5. Only after review run `./scripts/prepare-public-history.ps1 -Apply`. It creates
   local `public-main` with exactly one parentless `Initial public release` commit
   containing the clean HEAD tree. It leaves the checkout/index unchanged and
   requires the committed license and HISTORY PREPARATION STATUS: READY marker, and
   refuses an existing public-main. PUBLIC RELEASE STATUS may remain BLOCKED: local
   root creation and remote publication readiness are deliberately separate.
6. Inspect and test that branch:

   ```powershell
   git show --stat public-main
   git rev-list --count public-main
   git rev-list --parents public-main
   git switch public-main
   ./publish.ps1
   ./native/FH2ML.AzimuthRPC/build.ps1
   ./scripts/audit-public-tree.ps1
   ./tests/public-history-tests.ps1
   ```

7. After reviewing the root/tree and artifacts, push the orphan branch:

   ```powershell
   git push origin public-main
   git ls-remote --heads origin main
   ```

8. If remote main is absent, create it from the root: `git push origin public-main:main`.
   If it exists, stop and review its contents/protection; any replacement requires
   a separate explicit maintainer decision. Do not merge private history into it.
9. In GitHub Settings → Branches set main as default. Review protections/rulesets.
   Remove refactor/codex-cleanup from the CI PR branch filter in a new PR targeting
   main, keeping ordinary PR permissions at contents:read.
10. Review the branch table in PUBLICATION_AUDIT.md and archive anything needed
    privately. Delete only individually reviewed old remote branches, including
    the preparation branch, after explicit maintainer approval. The command form
    is `git push origin --delete <reviewed-branch>`. No script does this for you.
11. Review every old tag and GitHub Release. All tags listed in the audit reach
    private development commits. After explicit approval, remove/recreate affected
    Releases/tags on the new public history rather than keeping old source ZIPs
    or binaries whose provenance has not been cleared. Review Actions artifacts,
    logs, issues and PR attachments too.
12. Check advertised refs, PR refs, commit URLs and GitHub UI before changing
    visibility:

    ```powershell
    git ls-remote origin
    git ls-remote origin 'refs/pull/*'
    git log --all --oneline --decorate
    ```

    A local clone still contains private backup refs; audit remote reachability
    in a fresh clone. Creating a new orphan main alone is **not enough** while old
    branches/tags expose previous commits. Even deleting advertised refs does not
    guarantee erasure of PR refs, cached SHA views, forks or other clones. GitHub
    documents these limits in [removing sensitive data](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository).
    If private material remains accessible, stop; use GitHub's supported review/
    support route where applicable. Support may decline non-sensitive removals.
13. Only after those checks and a final manual review change visibility in GitHub
    Settings → General → Danger Zone. Review GitHub's [visibility consequences](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/managing-repository-settings/setting-repository-visibility).

Do not run release workflows or push version tags during this preparation. Existing
Releases and tags are audited, not altered. Switching CI to main should be a
follow-up after the default changes; current cleanup PRs must continue to build.

#!/usr/bin/env bash
# Run the GitHub Actions Windows jobs on the real Windows host before pushing.
#
#   scripts/ci-local/preflight.sh                 # architecture gates + full CI job
#   scripts/ci-local/preflight.sh --suite Quick   # restore, build, A1/A2, tests
#   scripts/ci-local/preflight.sh --dirty         # include uncommitted changes
#   scripts/ci-local/preflight.sh --resume        # re-run without re-syncing
#   scripts/ci-local/preflight.sh --rev <commit>  # test a commit, ignore the tree
#
# The commit under test travels as a git bundle over SSH, so nothing has to be
# pushed to GitHub to be tested. Submodules are fetched from GitHub by the
# Windows host at exactly the commits this tree records, which is what
# actions/checkout with `submodules: recursive` does.
#
# Remote work is always invoked as `pwsh -File <script>`. Passing pwsh code
# through an inline `-Command` string means it survives zsh quoting, ssh
# quoting and then PowerShell parsing; `-File` with real parameters does not
# have that problem.
set -euo pipefail

REMOTE="${MPT_CI_HOST:-win}"
WS_WIN='C:\ci\MyPowerTools'
WS_POSIX='C:/ci/MyPowerTools'
DROP_WIN='C:\ci\incoming'
DROP_POSIX='C:/ci/incoming'
ORIGIN_URL='https://github.com/dqtz5vpvj9-create/MyPowerTools.git'

suite='Both'
allow_dirty=0
resume=0
rev=''  

while [[ $# -gt 0 ]]; do
    case "$1" in
        --suite)   suite="$2"; shift 2 ;;
        --dirty)   allow_dirty=1; shift ;;
        --resume)  resume=1; shift ;;
        --rev)     rev="$2"; shift 2 ;;
        --host)    REMOTE="$2"; shift 2 ;;
        -h|--help) sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

run_ci() {
    ssh "$REMOTE" "pwsh -NoLogo -NoProfile -File ${WS_WIN}\\scripts\\ci-local\\Invoke-WindowsCi.ps1" \
        "-Workspace ${WS_WIN} -Suite ${suite} -SkipSync" \
        "-ReportPath ${WS_WIN}\\artifacts\\ci-local-report.json"
}

collect_report() {
    mkdir -p artifacts
    if scp -q "$REMOTE:$WS_POSIX/artifacts/ci-local-report.json" artifacts/ci-local-report.json 2>/dev/null; then
        echo "==> report saved to artifacts/ci-local-report.json"
    fi
}

if [[ $resume -eq 1 ]]; then
    set +e; run_ci; status=$?; set -e
    collect_report
    exit $status
fi

echo "==> linting workflows"
scripts/ci-local/lint-workflows.sh

# ---------------------------------------------------------------------------
# Decide what commit is under test.
# ---------------------------------------------------------------------------

work="$(mktemp -d)"
sm_bundles=()
trap 'rm -rf "$work"; git update-ref -d refs/preflight/head 2>/dev/null || true' EXIT

# An explicit --rev tests exactly that commit and never reads the working tree,
# which is what you want for a commit built with plumbing, or to check out a
# branch's state without disturbing whatever is in progress here.
if [[ -n "$rev" ]]; then
    target="$(git rev-parse --verify "$rev^{commit}")"
    echo "==> testing $target ($(git log --oneline -1 --format=%s "$target"))"
    dirty_lines=0
    allow_dirty=0
    skip_tree=1
else
    skip_tree=0
fi

dirty_lines="${dirty_lines:-$(git status --porcelain | wc -l)}"
if [[ $skip_tree -eq 0 && "$dirty_lines" -gt 0 && $allow_dirty -eq 0 ]]; then
    echo "working tree has $dirty_lines modified paths." >&2
    echo "commit them, or pass --dirty to test the working tree as a throwaway commit." >&2
    exit 1
fi

if [[ $allow_dirty -eq 1 && "$dirty_lines" -gt 0 ]]; then
    # Build the commit the working tree would produce if everything were
    # committed, using a throwaway index so HEAD, the real index and the stash
    # list are untouched. `git add -A` against that index picks up new files as
    # well as modified ones and still honours .gitignore, which `git stash
    # create` does not -- it ignores untracked files entirely, so a brand new
    # script would be missing from the tree under test.
    tmp_index="$work/index"
    GIT_INDEX_FILE="$tmp_index" git read-tree HEAD
    GIT_INDEX_FILE="$tmp_index" git add -A
    tree="$(GIT_INDEX_FILE="$tmp_index" git write-tree)"
    target="$(git commit-tree "$tree" -p HEAD -m 'preflight: working tree')"
    echo "==> testing working tree as $target ($dirty_lines modified paths)"
elif [[ $skip_tree -eq 0 ]]; then
    target="$(git rev-parse HEAD)"
    echo "==> testing $target ($(git log --oneline -1 --format=%s))"
fi

# ---------------------------------------------------------------------------
# Submodules. A bundle of the superproject carries only each submodule's
# recorded commit, so an uncommitted change inside one -- which is where all ten
# tools live -- would silently not be under test at all. Each dirty submodule
# gets the same throwaway-commit treatment and travels alongside.
# ---------------------------------------------------------------------------

sm_manifest="$work/submodules.tsv"
: > "$sm_manifest"

if [[ $allow_dirty -eq 1 ]]; then
    while read -r sm; do
        [[ -n "$sm" ]] || continue
        [[ -n "$(git -C "$sm" status --porcelain)" ]] || continue

        sm_slug="${sm//\//-}"
        sm_index="$work/index-$sm_slug"
        GIT_INDEX_FILE="$sm_index" git -C "$sm" read-tree HEAD
        GIT_INDEX_FILE="$sm_index" git -C "$sm" add -A
        sm_tree="$(GIT_INDEX_FILE="$sm_index" git -C "$sm" write-tree)"
        sm_commit="$(git -C "$sm" commit-tree "$sm_tree" -p HEAD -m 'preflight: working tree')"

        git -C "$sm" update-ref refs/preflight/head "$sm_commit"
        sm_bundle="$work/sm-$sm_slug.bundle"
        git -C "$sm" bundle create "$sm_bundle" refs/preflight/head --not --remotes=origin >/dev/null 2>&1 \
            || git -C "$sm" bundle create "$sm_bundle" refs/preflight/head >/dev/null 2>&1
        git -C "$sm" update-ref -d refs/preflight/head 2>/dev/null || true

        echo "==> submodule $sm carries local changes as $sm_commit"
        printf '%s\t%s\t%s\n' "$sm" "$sm_commit" "${DROP_WIN}\\sm-$sm_slug.bundle" >> "$sm_manifest"
        sm_bundles+=("$sm_bundle")
    done < <(git submodule foreach --quiet 'echo $sm_path')
fi

# ---------------------------------------------------------------------------
# Transport. `git bundle` needs a named ref, not a bare SHA, so the commit gets
# one; `--not --remotes=origin` then keeps the bundle thin.
# ---------------------------------------------------------------------------

git update-ref refs/preflight/head "$target"

bundle_arg=()
if git bundle create "$work/preflight.bundle" refs/preflight/head --not --remotes=origin >/dev/null 2>&1; then
    echo "==> shipping $(du -h "$work/preflight.bundle" | cut -f1) bundle"
    ssh "$REMOTE" "pwsh -NoLogo -NoProfile -Command \"New-Item -ItemType Directory -Path '${DROP_WIN}' -Force | Out-Null\""
    scp -q "$work/preflight.bundle" "$REMOTE:$DROP_POSIX/preflight.bundle"
    bundle_arg=("-BundlePath" "${DROP_WIN}\\preflight.bundle")
else
    # Empty bundle: every commit is already on origin, so the host can fetch it.
    echo "==> commit is already on origin"
    ssh "$REMOTE" "pwsh -NoLogo -NoProfile -Command \"New-Item -ItemType Directory -Path '${DROP_WIN}' -Force | Out-Null\""
fi

scp -q scripts/ci-local/Sync-CiWorkspace.ps1 "$REMOTE:$DROP_POSIX/Sync-CiWorkspace.ps1"

sm_arg=()
if [[ -s "$sm_manifest" ]]; then
    scp -q "${sm_bundles[@]}" "$REMOTE:$DROP_POSIX/"
    scp -q "$sm_manifest" "$REMOTE:$DROP_POSIX/submodules.tsv"
    sm_arg=("-SubmoduleManifest" "${DROP_WIN}\\submodules.tsv")
fi

# external/NotifyApp is private. The workflows hand actions/checkout
# secrets.MPT_SUBMODULE_PAT for it; locally the token comes from the gh CLI and
# travels over stdin so it never appears in a command line on either machine.
token=''
if command -v gh >/dev/null 2>&1; then
    token="$(gh auth token 2>/dev/null || true)"
fi
if [[ -z "$token" ]]; then
    echo "warning: no gh token available; the private submodule will fail to clone." >&2
fi

echo "==> syncing $REMOTE:$WS_WIN"
printf '%s\n' "$token" | ssh "$REMOTE" "pwsh -NoLogo -NoProfile -File ${DROP_WIN}\\Sync-CiWorkspace.ps1" \
    "-Commit ${target} -Workspace ${WS_WIN} -OriginUrl ${ORIGIN_URL} -TokenFromStdin" \
    "${bundle_arg[@]}" "${sm_arg[@]}"

echo "==> running suite $suite on $REMOTE"
set +e; run_ci; status=$?; set -e
collect_report
exit $status

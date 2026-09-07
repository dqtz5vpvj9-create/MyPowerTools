#!/usr/bin/env bash
# Lint every GitHub Actions workflow with actionlint before pushing.
#
# actionlint catches the failure class that costs a full runner round-trip to
# discover: YAML that parses but is wrong. Unknown runner labels, `${{ }}`
# expressions referencing outputs or secrets that do not exist, `needs` on a job
# that is not declared, bad `if:` syntax, shell quoting bugs inside `run:`.
#
# It does NOT run the jobs. Windows and macOS behaviour is checked by
# preflight.sh against the real Windows host.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
version="1.7.7"
cache_dir="${XDG_CACHE_HOME:-$HOME/.cache}/mypowertools-ci-local"
binary="$cache_dir/actionlint-$version"

if [[ ! -x "$binary" ]]; then
    echo "==> installing actionlint $version into $cache_dir"
    mkdir -p "$cache_dir"
    case "$(uname -s)/$(uname -m)" in
        Linux/x86_64)  asset="actionlint_${version}_linux_amd64.tar.gz" ;;
        Linux/aarch64) asset="actionlint_${version}_linux_arm64.tar.gz" ;;
        Darwin/arm64)  asset="actionlint_${version}_darwin_arm64.tar.gz" ;;
        Darwin/x86_64) asset="actionlint_${version}_darwin_amd64.tar.gz" ;;
        *) echo "unsupported platform $(uname -s)/$(uname -m)" >&2; exit 1 ;;
    esac
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' EXIT
    curl -fsSL -o "$tmp/actionlint.tgz" \
        "https://github.com/rhysd/actionlint/releases/download/v${version}/${asset}"
    tar xzf "$tmp/actionlint.tgz" -C "$tmp" actionlint
    mv "$tmp/actionlint" "$binary"
    chmod +x "$binary"
fi

# shellcheck is optional; when present actionlint uses it to check the bash in
# `run:` blocks. Most of this repository's steps are pwsh, so it adds little,
# but it is free when installed.
shellcheck_arg=()
if ! command -v shellcheck >/dev/null 2>&1; then
    shellcheck_arg=(-shellcheck '')
fi

cd "$repo_root"
echo "==> actionlint $version over .github/workflows"
"$binary" -no-color -oneline "${shellcheck_arg[@]}" "$@"
echo "==> workflows are clean"

#!/usr/bin/env bash
#
# Cuts a release: verify, tag, push. Publishing itself is the release workflow's job -
# this script's only privilege is creating a tag, so a mistake here cannot push a package.
#
#   scripts/release.sh 0.2.0
#
# No build framework. The four commands below are the whole process, and a shell script that
# runs them in order is easier to read than a DSL that wraps them.

set -euo pipefail

version="${1:-}"

if [[ -z "$version" ]]; then
    echo "usage: scripts/release.sh <version>    e.g. scripts/release.sh 0.2.0" >&2
    exit 1
fi

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    echo "'$version' is not a version. Expected 1.2.3, optionally with a -prerelease suffix." >&2
    exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

if [[ -n "$(git status --porcelain)" ]]; then
    echo "The working tree is dirty. Commit or stash first - a release should be a commit that exists." >&2
    exit 1
fi

tag="v$version"

if git rev-parse "$tag" >/dev/null 2>&1; then
    echo "$tag already exists. Versions are not reused." >&2
    exit 1
fi

echo "==> Formatting"
dotnet format --verify-no-changes

echo "==> Building $version"
dotnet build --configuration Release "-p:Version=$version"

echo "==> Testing (everything, including the slow ones)"
dotnet test --no-build --configuration Release

echo "==> Building the examples"
dotnet build examples/Examples.slnx --configuration Release

echo "==> Packing"
rm -rf ./artifacts
dotnet pack --no-build --configuration Release "-p:Version=$version" --output ./artifacts

ls -1 ./artifacts

echo
echo "==> Tagging $tag"
git tag -a "$tag" -m "Autobahn $version"

echo
echo "Done. The packages above are what will be published."
echo "Push the tag when you are ready - that is what triggers the release workflow:"
echo
echo "    git push origin $tag"

#!/usr/bin/env bash
#
# Builds the Tesserae web UI and stages it where the CLI embeds it from.
#
# Kept out of `dotnet build` on purpose: compiling the UI needs the Transpose compiler
# installed as a global tool, and a clean clone does not have one. Everything else in the
# repository builds with no arguments, and this is the one step that asks for something first.
#
# The staged files are gzipped. They are embedded into the CLI assembly, which stores them
# uncompressed, and the difference between three megabytes and twelve is the difference
# between a dotnet tool people install without thinking and one they notice.

set -euo pipefail

configuration="${1:-Release}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
staged="$root/src/Autobahn.Cli/Ui/wwwroot"
compiled="$root/src/Autobahn.Ui/bin/$configuration/netstandard2.0/tps"

if ! command -v tps >/dev/null 2>&1; then
    echo "The Transpose compiler is not on PATH."
    echo "  dotnet tool update --global Transpose.Compiler"
    echo "  export PATH=\"\$PATH:\$HOME/.dotnet/tools\""
    exit 1
fi

echo "Building the UI ($configuration)…"
dotnet build -c "$configuration" "$root/src/Autobahn.Ui/Autobahn.Ui.slnx"

if [ ! -f "$compiled/index.html" ]; then
    echo "The Transpose compiler produced no index.html in $compiled." >&2
    exit 1
fi

echo "Staging into ${staged#"$root/"}…"
rm -rf "$staged"
mkdir -p "$staged"

# The manifest is the compiler's own incremental-build bookkeeping, not part of the site.
(
    cd "$compiled"
    find . -type f ! -name '.tps-manifest*' -print0 | while IFS= read -r -d '' file; do
        relative="${file#./}"
        mkdir -p "$staged/$(dirname "$relative")"
        gzip -9 -c "$file" > "$staged/$relative.gz"
    done
)

echo "Staged $(find "$staged" -type f | wc -l | tr -d ' ') files, $(du -sh "$staged" | cut -f1)."
echo "Build the CLI now and the UI travels with it."

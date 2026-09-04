#!/usr/bin/env bash
# Regenerates docs/html/ from the Markdown sources.
# Run this after ANY change to a .md file — the HTML mirror is generated, never hand-edited.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
echo "docs-build: regenerating docs/html/ from Markdown"
dotnet run --project "$root/tools/DocsBuild/DocsBuild.csproj" --verbosity quiet -- "$root"

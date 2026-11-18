#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
# Builds the networking core and enabled/disabled Shard assemblies, then
# installs the built Shards under shards/installed/<id>/.
dotnet build Amethyst.slnx --nologo >&2
exec dotnet run --no-build --project src/Amethyst.csproj -- "${1:-server.yaml}"

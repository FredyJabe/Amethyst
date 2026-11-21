#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
# Fetch the version-pinned 26.3 vanilla loot dataset (only on first checkout).
# Runtime never fetches unverified, floating Minecraft data.
if [[ -f .gitmodules && ! -f vendor/minecraft-26.3-data/_meta.json ]]; then
  git submodule update --init --depth 1 vendor/minecraft-26.3-data
fi
# Builds the networking core and enabled/disabled Shard assemblies, then
# installs the built Shards under shards/installed/<id>/.
dotnet build Amethyst.slnx --nologo >&2
exec dotnet run --no-build --project src/Amethyst.csproj -- "${1:-server.yaml}"

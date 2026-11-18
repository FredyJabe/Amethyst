# Third-party data and acknowledgments

The Amethyst server implementation is independently written in C# / .NET.

- **Minecraft Java Edition 26.3 protocol:** the protocol numbers, registry names,
  and block-state identifiers used for interoperability are based on publicly
  observable Minecraft client/server protocol metadata.
- **Version-specific registry ordering:** the checked-in `src/VanillaRegistries/registry_entries.txt`
  was originally assembled from the Java 26.3 registry snapshot in
  [ezchr/go-mcjava](https://github.com/ezchr/go-mcjava). Refer to that project's
  license for the upstream work and metadata.
- **Vanilla update-tags payload:** `tools/fetch_vanilla_tags.py` downloads
  a version-pinned file from `ezchr/go-mcjava` at runtime or during setup.
  The fetched binary is intentionally excluded from Git; no upstream binary
  data is checked in.
- **Creative palette, Survival mining hardness and item/state mappings:** the version-pinned, curated simple-block
  subset in `CreativeBlockPalette.cs` was generated from
  [RosegoldMC/minecraft-data.cr](https://github.com/RosegoldMC/minecraft-data.cr)
  `data/26.3/items.json` and `data/26.3/blocks.json`. The source dataset
  and its upstream notices should be consulted when updating protocol versions.
- **YamlDotNet:** external NuGet dependency; see
  [YamlDotNet](https://github.com/aaubry/YamlDotNet) for its license.

Minecraft and related names belong to Mojang Studios / Microsoft. This project
is unofficial and is not affiliated with Mojang or Microsoft.

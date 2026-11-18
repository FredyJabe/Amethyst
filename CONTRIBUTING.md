# Contributing to Amethyst

Contributions are welcome. Please discuss major protocol or world-generation
changes in an issue first. The current project targets Java Edition 26.3 and
.NET 10.

1. Keep existing `generatorVersion` outputs deterministic unless explicitly
   introducing a new generator version.
2. Preserve the packet-777 framing/encoding regression tests.
3. Run `dotnet build Amethyst.slnx` and relevant `tests/*/*.csproj`
   executable tests. Python source checks require `pyyaml`.
4. Do not submit operator lists, world files, authentication tokens, Minecraft
   binaries, or locally generated registry payloads.

This project is experimental and breaking API changes may occur.

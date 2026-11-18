# Security policy

Amethyst is an experimental protocol implementation, not a security-audited public server.

- Keep `onlineMode: true` if exposing the server to the internet.
- Avoid running the process as root or storing server state in system directories.
- Do not commit `ops.yaml`, saved worlds, private configuration, or tokens.
- Keep client and protocol compatibility pinned to Java Edition 26.3.
- Report security problems privately to the repository maintainers rather than posting working exploits in public issues.

The project does not yet provide production-grade denial-of-service protections or complete gameplay validation.

# Contributing

Contributions are welcome.

## Ground rules

- Keep changes surgical and tied to a reported issue or clear feature request.
- Do not commit secrets, private keys, generated signing artifacts, or build output.
- `opencode.jsonc` is local configuration and is ignored. Use `opencode.example.jsonc` as the template.
- Driver and engine changes must stay compatible with the WBDI/WBF contracts documented in `docs/SETUP.md` and the code comments.

## Development loop

1. Build affected projects.
2. Run the relevant probe or tool under `tests/`.
3. If driver/package files change, regenerate the catalog using `package/README.md`.
4. Use `scripts/Refresh-VirtualFingerprint.ps1` to reinstall rebuilt binaries.
5. Document behavior changes in `README.md` or `docs/SETUP.md`.

## Pull requests

- Describe what changed and how you verified it.
- Include Windows version, SDK/WDK version, and exact build commands when reporting driver issues.
- Do not include `*.pfx`, `*.cat`, `fingerprints.json`, or local MCP configuration.

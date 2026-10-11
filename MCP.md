# FModel MCP server

This fork embeds a [Model Context Protocol](https://modelcontextprotocol.io/) server in FModel so an AI
assistant (Claude Code, Claude Desktop, OpenAI Codex, ChatGPT, Cursor, ...) can browse, read and export
Unreal Engine assets through the running FModel instance, on your command.

- Transport: Streamable HTTP, loopback only
- Endpoint: `http://127.0.0.1:8650/mcp` (port configurable in Settings > General > AI Assistants)
- Starts with FModel; toggle or change the port in Settings, no restart needed
- Every tool call is echoed in FModel's log panel with an `[MCP]` prefix

FModel must be running with a game selected. The AI uses the same provider, AES keys, mappings and export
settings as the UI, and exports land in the same output folders.

## Connect a client

### Claude Code

```
claude mcp add --transport http fmodel http://127.0.0.1:8650/mcp
```

Or add it to `.mcp.json` in a project / `~/.claude.json`:

```json
{
  "mcpServers": {
    "fmodel": { "type": "http", "url": "http://127.0.0.1:8650/mcp" }
  }
}
```

### Claude Desktop

Claude Desktop launches stdio servers only, so bridge with `mcp-remote` in `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "fmodel": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:8650/mcp", "--allow-http"]
    }
  }
}
```

### OpenAI Codex CLI

In `~/.codex/config.toml`:

```toml
[mcp_servers.fmodel]
url = "http://127.0.0.1:8650/mcp"
```

### ChatGPT and other remote-only clients

ChatGPT's connectors need a public HTTPS URL. Expose the loopback port through a tunnel you control
(for example `ngrok http 8650` or `cloudflared tunnel --url http://127.0.0.1:8650`) and register
`https://<your-tunnel>/mcp` as a custom connector. Anyone with the URL can drive your FModel while the tunnel
is up, so prefer the local clients above when you can.

### Verify

```
curl -s http://127.0.0.1:8650/
```

prints `FModel <version> MCP server. Streamable HTTP endpoint: /mcp`.

Without any AI in the loop, `tools/mcp-smoke-test.mjs` (Node 18+) speaks the protocol directly:

```
node tools/mcp-smoke-test.mjs http://127.0.0.1:8650/mcp status {} search_files '{"query":"DefaultGame.ini"}'
```

## Tools

| Tool | What it does |
|------|--------------|
| `status` | Game, UE version, archives, AES keys, loaded packages, output folders, busy/ready |
| `list_archives` | Every pak/utoc with mounted/encrypted state and key GUID |
| `load_packages` | Same as the Load button: populates the explorer, loads localized text and virtual paths |
| `list_directory` | Sub-folders and files of a virtual directory |
| `search_files` | Multi-term, regex, extension and directory filtered path search with paging |
| `get_file_info` | Size, archive, and export list (names + classes) of a package |
| `read_package_json` | The JSON FModel shows in its property viewer; whole package or one export; can save to disk |
| `read_text` | ini, json, locres/locmeta, AssetRegistry.bin, uplugin, verse, lua, ... as text |
| `get_texture` | Decodes a texture or image file and returns a PNG the model can look at |
| `export_assets` | Export given files: raw, properties, textures, models, animations, worlds, audio, code |
| `export_folder` | Same for a whole virtual folder, optional extension filter |
| `open_in_ui` | Double-click equivalent: opens JSON tab, texture preview, 3D viewer or audio player in FModel |
| `find_references` | Packages referencing a package |
| `decompile_blueprint` | Pseudo C++ of a Blueprint class |
| `get_settings` / `update_settings` | Read and change export formats and output directories |
| `set_aes_key` | Provide an AES key for the main key or one archive and remount |
| `cancel` | Cancel the running FModel operation / export session |

Paths are virtual archive paths such as `FortniteGame/Content/Athena/Items/Cosmetics/Characters/CID_001.uasset`;
`/Game/Athena/...` is accepted as well.

## Example prompts

- "Find every skeletal mesh under `/Game/Characters/Player` and export them as glTF to `D:\Exports`."
- "Show me the icon of `CID_028_Athena_Commando_F` and tell me what rarity the item definition says."
- "Dump `DefaultGame.ini` and list all GameFeature plugins that are enabled."
- "Which assets reference `M_Master_Character`?"

## How it works

- `FModel/Mcp/McpServerHost.cs` hosts Kestrel + `ModelContextProtocol.AspNetCore` inside the WPF process,
  bound to `127.0.0.1` with a loopback `Host` header check.
- `FModel/Mcp/FModelMcpTools.cs` declares the tools (attribute based, static methods).
- `FModel/Mcp/McpBridge.cs` does the work: it reads through the CUE4Parse provider directly for read-only
  tools and funnels anything that mutates state (exports, loading, opening in UI) through FModel's single
  worker queue, waiting for user-started operations to finish and never running two MCP jobs at once.
- Tool failures are returned as MCP tool errors with a message the model can react to.

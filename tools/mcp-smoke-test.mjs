// Minimal Streamable-HTTP MCP client for poking the FModel MCP server without an AI in the loop.
// usage: node tools/mcp-smoke-test.mjs [url] [toolName [jsonArgs]]...
// e.g.   node tools/mcp-smoke-test.mjs http://127.0.0.1:8650/mcp status {} search_files '{"query":"DefaultGame.ini"}'
const url = process.argv[2] ?? "http://127.0.0.1:8650/mcp";
let id = 0;

async function rpc(method, params) {
  const res = await fetch(url, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json, text/event-stream" },
    body: JSON.stringify({ jsonrpc: "2.0", id: ++id, method, params }),
  });
  const ct = res.headers.get("content-type") ?? "";
  const text = await res.text();
  if (!res.ok) throw new Error(`${method}: HTTP ${res.status} ${text.slice(0, 500)}`);
  if (ct.includes("text/event-stream")) {
    const msgs = text.split(/\r?\n/).filter(l => l.startsWith("data:")).map(l => JSON.parse(l.slice(5).trim()));
    return msgs.find(m => m.id === id) ?? msgs.at(-1);
  }
  return JSON.parse(text);
}

const init = await rpc("initialize", {
  protocolVersion: "2025-06-18",
  capabilities: {},
  clientInfo: { name: "mcp-test", version: "0.0.1" },
});
console.log("server:", JSON.stringify(init.result?.serverInfo), "protocol:", init.result?.protocolVersion);
console.log("instructions:", (init.result?.instructions ?? "").split("\n")[0], "...");

const tools = await rpc("tools/list", {});
console.log(`tools (${tools.result.tools.length}):`, tools.result.tools.map(t => t.name).join(", "));

const calls = process.argv.slice(3);
for (let i = 0; i < calls.length; i += 2) {
  const name = calls[i];
  const args = calls[i + 1] ? JSON.parse(calls[i + 1]) : {};
  const t0 = Date.now();
  const r = await rpc("tools/call", { name, arguments: args });
  const ms = Date.now() - t0;
  if (r.error) { console.log(`\n### ${name} -> RPC ERROR`, JSON.stringify(r.error)); continue; }
  const res = r.result;
  console.log(`\n### ${name} ${JSON.stringify(args)} (${ms} ms) isError=${res.isError ?? false}`);
  for (const c of res.content ?? []) {
    if (c.type === "text") console.log(c.text.length > 3000 ? c.text.slice(0, 3000) + `\n...[${c.text.length} chars]` : c.text);
    else if (c.type === "image") console.log(`[image ${c.mimeType}, ${Math.round(c.data.length * 3 / 4 / 1024)} KiB base64-decoded]`);
    else console.log(JSON.stringify(c).slice(0, 500));
  }
}

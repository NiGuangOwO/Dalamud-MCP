// Talks to the REAL Dalamud-MCP server using the OFFICIAL @modelcontextprotocol/sdk (v1.30.1).
//
// Why this matters: every check in tests\* speaks JSON-RPC that this project's author wrote by
// hand. If the framing were misread - a wrong header name, a session id in the wrong place, a
// response shape that only this project emits - those tests would agree with the server's own
// mistake and still pass. This client is somebody else's implementation of the same spec, so it
// cannot share that blind spot.
//
// Usage: node interop.mjs <port>

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { isBlockedPort } from "./blocked-ports.mjs";

const port = Number(process.argv[2]);
if (!port) {
  console.error("usage: node interop.mjs <port>");
  process.exit(2);
}

// Fail with a diagnosis instead of a bare "fetch failed". Node's fetch refuses these ports
// before connecting, so if the host ever lands on one the symptom is an opaque TypeError that
// looks like the server is broken. (The host avoids them; this is the backstop.)
if (isBlockedPort(port)) {
  console.error(
    `port ${port} is in the WHATWG fetch "bad port" list, which Node refuses before connecting; ` +
      "the host should have bound elsewhere. This is a harness problem, not a server problem.",
  );
  process.exit(2);
}

const url = new URL(`http://127.0.0.1:${port}/mcp`);
let checks = 0;
let failures = 0;

function check(label, ok, detail = "") {
  checks++;
  if (ok) {
    console.log(`  [PASS] ${label}`);
  } else {
    failures++;
    console.log(`  [FAIL] ${label} -> ${detail}`);
  }
}

function firstLine(text, limit = 300) {
  if (text === null || text === undefined) return "(empty)";
  const s = String(text).replace(/\r?\n/g, " ⏎ ");
  return s.length > limit ? s.slice(0, limit) + "…" : s;
}

const transport = new StreamableHTTPClientTransport(url);
const client = new Client({ name: "dalamud-mcp-interop", version: "1.0.0" });

try {
  // The handshake: the SDK negotiates the protocol version and, for Streamable HTTP,
  // establishes the session id from the response headers. If the server's header name or
  // initialize result shape were wrong, this throws.
  await client.connect(transport);
  // The awaited call IS the assertion: a framing mistake (wrong session header, unexpected
  // initialize result shape) throws, and the catch below scores it as a failure.
  check("the official SDK completes initialize against the real server", true);

  // The server identifies itself in initialize; assert the identity actually arrived rather
  // than merely that some field exists on the parsed result.
  const version = client.getServerVersion();
  check(
    "initialize returned the server's identity",
    version?.name === "dalamud-mcp" && typeof version?.version === "string",
    firstLine(JSON.stringify(version)),
  );
  console.log(`  [note] server version: ${firstLine(JSON.stringify(version))}`);

  // tools/list must parse into the SDK's declared shape.
  const tools = await client.listTools();
  check("the official SDK parses tools/list", Array.isArray(tools.tools), firstLine(JSON.stringify(tools).slice(0, 200)));

  const names = tools.tools.map((t) => t.name).sort();
  check(
    "tools/list returns every registered tool",
    names.join(",") === "echo,fail,ping,walk",
    `got: ${names.join(",")}`,
  );

  // The array-of-union schema: the shape that had the real defect. A client that chokes on it
  // would fail here rather than at 3am in an agent.
  const walk = tools.tools.find((t) => t.name === "walk");
  const offsets = walk?.inputSchema?.properties?.offsets;
  check(
    "the union schema survives an independent client's parser",
    offsets?.type === "array" &&
      Array.isArray(offsets?.items?.type) &&
      offsets.items.type.includes("integer") &&
      offsets.items.type.includes("string"),
    firstLine(JSON.stringify(offsets)),
  );

  const ping = tools.tools.find((t) => t.name === "ping");
  check(
    "a no-parameter tool advertises an empty property set",
    ping !== undefined && Object.keys(ping.inputSchema.properties ?? {}).length === 0,
    firstLine(JSON.stringify(ping?.inputSchema)),
  );

  // A real call through the SDK's own argument serialisation.
  const echoed = await client.callTool({ name: "echo", arguments: { text: "hello from the official SDK" } });
  const text = echoed.content?.[0]?.text ?? "";
  check("the official SDK carries a tool call through", text.includes("hello from the official SDK"), firstLine(text));

  // Hex-string offsets, the form the schema advertises.
  const walked = await client.callTool({ name: "walk", arguments: { address: "0x1000", offsets: ["0x10", 448] } });
  const walkText = walked.content?.[0]?.text ?? "";
  check(
    "an array of mixed integer/hex-string offsets round-trips",
    walkText.includes("0x10") && walkText.includes("448"),
    firstLine(walkText),
  );

  // Tool errors must arrive as a result with isError, not as a transport failure - otherwise
  // an agent cannot tell "the tool said no" from "the server broke".
  const failed = await client.callTool({ name: "fail", arguments: { reason: "because" } });
  check("a failing tool reports isError instead of breaking the transport", failed.isError === true, firstLine(JSON.stringify(failed)));
  check(
    "the failing tool's message reaches the client",
    (failed.content?.[0]?.text ?? "").includes("deliberate failure"),
    firstLine(failed.content?.[0]?.text),
  );

  // After an error, the session must still work.
  const after = await client.callTool({ name: "ping", arguments: {} });
  check("the session still works after a tool error", (after.content?.[0]?.text ?? "").includes("pong"), firstLine(after.content?.[0]?.text));

  await client.close();
  check("the official SDK closes the session cleanly", true);
} catch (error) {
  failures++;
  checks++;
  // Include the cause: a transport failure ("fetch failed" / ECONNREFUSED / bad port) and a
  // protocol rejection (ZodError naming a JSON-RPC field) are very different findings, and the
  // bare message alone does not distinguish them.
  const cause = error?.cause ? ` | cause: ${error.cause?.code ?? error.cause?.message ?? error.cause}` : "";
  console.log(`  [FAIL] the official SDK hit an unhandled error -> ${firstLine(error?.name ?? "Error")}: ${firstLine(error?.message ?? error)}${firstLine(cause)}`);
  if (error?.name === "ZodError") console.log(`  [note] conformance detail: ${firstLine(JSON.stringify(error.errors))}`);
}

console.log();
console.log(`${checks - failures}/${checks} interop checks passed`);
process.exit(failures === 0 ? 0 : 1);

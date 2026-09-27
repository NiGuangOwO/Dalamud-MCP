// NEGATIVE CONTROL for interop.mjs.
//
// interop.mjs reported 12/12 against the real server. That only means something if the official
// SDK would actually REJECT a server that does not conform - otherwise the passes are vacuous and
// prove nothing about this project's framing.
//
// This spins up a deliberately non-conforming HTTP server: it answers every POST to /mcp with
// 200 and a JSON body that is not a valid JSON-RPC initialize result, and never sends a session
// header. The official client must fail against it. If this script reports the client SUCCEEDED,
// then interop.mjs's passes are meaningless.

import http from "node:http";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { BLOCKED_PORTS } from "./blocked-ports.mjs";

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

// Ports that WHATWG fetch refuses outright, before any TCP connection is attempted. Node's
// built-in fetch implements that list, so a client pointed at one of these fails with
// "bad port" REGARDLESS of what is listening. That is a transport refusal, not a protocol
// rejection, and it must never be allowed to satisfy this control: it would make the control
// pass for a reason that has nothing to do with the SDK's conformance checking.
//
// The list itself lives in blocked-ports.mjs, shared with host\Program.cs, which must avoid
// these ports when it asks the server to bind.

// A server that is emphatically NOT an MCP server.
const fake = http.createServer((req, res) => {
  let body = "";
  req.on("data", (chunk) => (body += chunk));
  req.on("end", () => {
    res.writeHead(200, { "Content-Type": "application/json" });
    // Valid JSON, but not a JSON-RPC response: no jsonrpc field, no matching id, no result.
    res.end(JSON.stringify({ hello: "i am not an mcp server" }));
  });
});

// Bind until we get a port fetch will actually talk to, so the rejection below can only be
// about the protocol. Windows picks ephemeral ports from 1024-15000, which overlaps the
// blocked list, so a single bind is not guaranteed to be usable.
let port = 0;
for (let attempt = 0; attempt < 20; attempt++) {
  await new Promise((resolve) => fake.listen(0, "127.0.0.1", resolve));
  port = fake.address().port;
  if (!BLOCKED_PORTS.has(port)) break;
  await new Promise((resolve) => fake.close(resolve));
  port = 0;
}
if (!port) throw new Error("could not bind the fake server to a fetchable port");
console.log(`  [note] fake server on 127.0.0.1:${port}`);

try {
  const transport = new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`));
  const client = new Client({ name: "negative-control", version: "1.0.0" });
  await client.connect(transport);
  check("the official SDK REJECTS a non-conforming server", false, "connect() succeeded against a fake server");
  await client.close();
} catch (error) {
  // THE ASSERTION THAT MATTERS: it must be rejected BY THE SDK'S OWN CONFORMANCE CHECKING,
  // not merely fail to reach the server. A ZodError naming a JSON-RPC field is the SDK saying
  // "that response does not conform". A TypeError with no cause ("fetch failed") is the client
  // never getting an answer - which would make this control vacuous, exactly as it was when a
  // blocked port produced "fetch failed" and was scored as a pass.
  const structured = JSON.stringify(error?.errors ?? []);
  const isProtocolRejection =
    error?.name === "ZodError" && `${structured}${error?.message ?? ""}`.includes("jsonrpc");

  check(
    "the official SDK REJECTS a non-conforming server",
    isProtocolRejection,
    isProtocolRejection
      ? ""
      : `rejected for the wrong reason (${error?.name}: ${firstLine(error?.message ?? error)}) - ` +
        "the client may never have reached the server, which would make this control vacuous",
  );
  check(
    "the rejection is the SDK's conformance check, not a transport failure",
    error?.cause === undefined,
    firstLine(JSON.stringify(error?.cause ?? null)),
  );
  console.log(`  [note] rejected with: ${firstLine(error?.message ?? error)}`);
}

fake.close();

console.log();
console.log(`${checks - failures}/${checks} negative-control checks passed`);
process.exit(failures === 0 ? 0 : 1);

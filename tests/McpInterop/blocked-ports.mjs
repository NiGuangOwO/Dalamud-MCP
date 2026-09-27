// Ports that WHATWG fetch refuses outright, before any TCP connection is attempted.
//
// Why this file exists: Node's built-in fetch implements the WHATWG "bad port" list. A client
// pointed at one of these ports fails with a TypeError whose cause says "bad port", REGARDLESS
// of whether something is listening. Measured on this machine:
//
//   fetch('http://127.0.0.1:1719/x')              -> TypeError: fetch failed / cause: bad port
//   fetch('http://127.0.0.1:18777/x')             -> ECONNREFUSED
//   same, with a listener bound on 1719           -> still "bad port"
//
// That matters because Windows chooses ephemeral ports from 1024-15000, which OVERLAPS this
// list, so `listen(0)` can hand out a port that no Node client can talk to. A harness that
// ignored this would report a confusing "fetch failed" and - worse - a negative control could
// score a transport refusal as a successful protocol rejection.
//
// The same constant is duplicated in host\Program.cs, which must avoid these ports when it
// asks the server to bind. It is a fixed spec constant, so the two copies cannot drift.

export const BLOCKED_PORTS = new Set([
  1, 7, 9, 11, 13, 15, 17, 19, 20, 21, 22, 23, 25, 37, 42, 43, 53, 69, 77, 79, 87, 95, 101, 102,
  103, 104, 109, 110, 111, 113, 115, 117, 119, 123, 135, 137, 139, 143, 161, 179, 389, 427, 465,
  512, 513, 514, 515, 526, 530, 531, 532, 540, 548, 554, 556, 563, 587, 601, 636, 989, 990, 993,
  995, 1719, 1720, 1723, 2049, 3659, 4045, 4190, 5060, 5061, 6000, 6566, 6665, 6666, 6667, 6668,
  6669, 6679, 6697, 10080,
]);

export function isBlockedPort(port) {
  return BLOCKED_PORTS.has(Number(port));
}

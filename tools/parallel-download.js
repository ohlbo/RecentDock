// Parallel ranged downloader for hosts that throttle a single connection.
//
// Why Node and not PowerShell/curl on this machine:
//   1. schannel cannot acquire TLS credentials here
//      (AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS), so curl.exe and
//      Invoke-WebRequest fail on every HTTPS URL. Node links OpenSSL and works.
//   2. Invoke-WebRequest refuses to set the Range header ("必须使用适当的属性或
//      方法修改 Range 标头"), so it cannot do ranged downloads at all.
//
// Measured on the .NET CDN: one connection ~13 KB/s, 16 concurrent ranged
// connections ~450 KB/s (35x). The throttle is per-connection.
//
// Usage:
//   node parallel-download.js <url> <outFile> [connections] [chunkMiB]

const fs = require("fs");
const path = require("path");
const https = require("https");
const http = require("http");

const [, , url, outFile, connArg, chunkArg] = process.argv;

if (!url || !outFile) {
  console.error("usage: node parallel-download.js <url> <outFile> [connections] [chunkMiB]");
  process.exit(2);
}

const CONNECTIONS = parseInt(connArg || "16", 10);
const CHUNK = parseInt(chunkArg || "8", 10) * 1024 * 1024;
const MAX_RETRIES = 5;

// No keep-alive socket reuse. The probe request consumes one response and the
// server may close that socket; reusing it for the first chunk made the chunk
// request hang until timeout. Chunks are large enough that per-request
// connection setup is negligible, so fresh sockets are the safer trade.
const agent = new https.Agent({ keepAlive: false });

function request(targetUrl, headers, redirectsLeft = 5) {
  return new Promise((resolve, reject) => {
    const mod = targetUrl.startsWith("https") ? https : http;
    const req = mod.get(
      targetUrl,
      { headers: { "User-Agent": "RecentDock-build-setup", ...headers }, agent: targetUrl.startsWith("https") ? agent : undefined },
      (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirectsLeft > 0) {
          res.resume();
          const next = new URL(res.headers.location, targetUrl).toString();
          return resolve(request(next, headers, redirectsLeft - 1));
        }
        resolve(res);
      }
    );
    req.on("error", reject);
    req.setTimeout(300000, () => req.destroy(new Error("request timeout")));
  });
}

function fetchChunk(targetUrl, start, end) {
  return new Promise(async (resolve, reject) => {
    try {
      const res = await request(targetUrl, { Range: `bytes=${start}-${end}` });
      if (res.statusCode !== 206 && res.statusCode !== 200) {
        res.resume();
        return reject(new Error("HTTP " + res.statusCode));
      }
      const parts = [];
      let received = 0;
      res.on("data", (c) => {
        parts.push(c);
        received += c.length;
      });
      res.on("end", () => resolve(Buffer.concat(parts, received)));
      res.on("error", reject);
    } catch (e) {
      reject(e);
    }
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  console.log("probing " + url);
  const probe = await request(url, { Range: "bytes=0-0" });

  const contentRange = probe.headers["content-range"];
  let total = 0;
  if (contentRange && /\/(\d+)\s*$/.test(contentRange)) {
    total = parseInt(RegExp.$1, 10);
  } else if (probe.headers["content-length"]) {
    total = parseInt(probe.headers["content-length"], 10);
  }
  const rangesOk = probe.statusCode === 206;
  probe.resume();

  if (!total) {
    console.error("could not determine content length");
    process.exit(1);
  }
  if (!rangesOk) {
    console.error("server did not honour Range (status " + probe.statusCode + "); parallel download impossible");
    process.exit(1);
  }

  const chunkCount = Math.ceil(total / CHUNK);
  console.log(`  total       : ${(total / 1048576).toFixed(1)} MiB`);
  console.log(`  ranges      : supported (206)`);
  console.log(`  chunks      : ${chunkCount} x ${CHUNK / 1048576} MiB`);
  console.log(`  connections : ${CONNECTIONS}`);
  console.log("");

  fs.mkdirSync(path.dirname(path.resolve(outFile)), { recursive: true });
  const fd = fs.openSync(outFile, "w");
  fs.ftruncateSync(fd, total);

  const started = Date.now();
  let completed = 0;
  const failures = [];

  let next = 0;
  async function worker() {
    for (;;) {
      const index = next++;
      if (index >= chunkCount) return;

      const start = index * CHUNK;
      const end = Math.min(start + CHUNK - 1, total - 1);
      const expected = end - start + 1;

      let done = false;
      for (let attempt = 1; attempt <= MAX_RETRIES && !done; attempt++) {
        try {
          const buf = await fetchChunk(url, start, end);
          if (buf.length !== expected) {
            throw new Error(`short chunk: ${buf.length} != ${expected}`);
          }
          fs.writeSync(fd, buf, 0, buf.length, start);
          done = true;
        } catch (e) {
          if (attempt >= MAX_RETRIES) {
            failures.push(`chunk ${index}: ${e.message}`);
          } else {
            await sleep(1500 * attempt);
          }
        }
      }

      completed++;
      const secs = (Date.now() - started) / 1000;
      const moved = Math.min(completed * CHUNK, total);
      const pct = ((completed / chunkCount) * 100).toFixed(1).padStart(5);
      const rate = secs > 0 ? moved / secs : 0;
      const rateText = rate >= 1048576 ? (rate / 1048576).toFixed(2) + " MiB/s" : Math.round(rate / 1024) + " KB/s";
      process.stdout.write(`\r  [${pct}%] ${completed}/${chunkCount} chunks  ${rateText}   `);
    }
  }

  await Promise.all(Array.from({ length: Math.min(CONNECTIONS, chunkCount) }, () => worker()));
  process.stdout.write("\n");
  fs.closeSync(fd);

  if (failures.length) {
    console.error("\nFAILED chunks:");
    for (const f of failures) console.error("  " + f);
    console.error("Output is truncated; do not use it.");
    process.exit(1);
  }

  const stat = fs.statSync(outFile);
  if (stat.size !== total) {
    console.error(`size mismatch: expected ${total}, got ${stat.size}`);
    process.exit(1);
  }

  const secs = (Date.now() - started) / 1000;
  console.log("");
  console.log(`OK  ${outFile}`);
  console.log(`    ${(stat.size / 1048576).toFixed(1)} MiB in ${secs.toFixed(0)}s (${(stat.size / 1048576 / secs).toFixed(2)} MiB/s)`);
})().catch((e) => {
  console.error("FAILED: " + e.message);
  process.exit(1);
});

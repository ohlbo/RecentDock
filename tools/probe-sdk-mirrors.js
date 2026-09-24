// Second pass at finding a usable .NET SDK source.
//
// The first pass was misleading: Huawei reported 739 KB/s but had only received
// 12 KB before the stream ended, which means it served a small error page rather
// than the archive. A source is only credible if it actually declares a
// 200 MB+ content-length (or streams that much), so this pass only samples URLs
// that look like a real file, and reports the declared size.

const https = require("https");
const http = require("http");

const VERSION = "8.0.425";
const FILE = "dotnet-sdk-" + VERSION + "-win-x64.zip";

const CANDIDATES = [
  ["ms-azure-blob", `https://dotnetcli.blob.core.windows.net/dotnet/Sdk/${VERSION}/${FILE}`],
  ["huawei-flat", `https://mirrors.huaweicloud.com/dotnet/${FILE}`],
  ["huawei-versioned", `https://mirrors.huaweicloud.com/dotnet/${VERSION}/${FILE}`],
  ["huawei-repo", `https://repo.huaweicloud.com/dotnet/${FILE}`],
  ["aliyun", `https://mirrors.aliyun.com/dotnet/${FILE}`],
  ["ustc", `https://mirrors.ustc.edu.cn/dotnet/${FILE}`],
];

const SAMPLE_MS = 15000;
const MIN_PLAUSIBLE_BYTES = 150 * 1024 * 1024;

function probe(label, url, depth = 0) {
  return new Promise((resolve) => {
    let settled = false;
    const done = (result) => {
      if (settled) return;
      settled = true;
      resolve({ label, url, ...result });
    };

    const mod = url.startsWith("https") ? https : http;
    const req = mod.get(
      url,
      { headers: { "User-Agent": "RecentDock-build-setup" } },
      (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && depth < 5) {
          res.resume();
          return probe(label, res.headers.location, depth + 1).then(done);
        }
        if (res.statusCode !== 200) {
          res.resume();
          return done({ ok: false, note: "HTTP " + res.statusCode });
        }

        const declared = parseInt(res.headers["content-length"] || "0", 10);

        // Reject small responses outright: they are error pages or HTML, not an SDK.
        if (declared > 0 && declared < MIN_PLAUSIBLE_BYTES) {
          res.resume();
          return done({ ok: false, note: "only " + declared + " bytes declared (not an SDK archive)" });
        }

        let bytes = 0;
        const started = Date.now();

        const finish = (note) => {
          const secs = Math.max((Date.now() - started) / 1000, 0.001);
          done({
            ok: true,
            sampled: bytes,
            secs: Math.round(secs),
            kbps: Math.round(bytes / secs / 1024),
            declared,
            note,
          });
        };

        res.on("data", (chunk) => {
          bytes += chunk.length;
          if (Date.now() - started >= SAMPLE_MS) {
            res.destroy();
            finish("sampled");
          }
        });
        res.on("end", () => finish("stream ended"));
        res.on("error", () => (bytes > 0 ? finish("aborted") : done({ ok: false, note: "stream error" })));
      }
    );

    req.on("error", (e) => done({ ok: false, note: e.message }));
    req.setTimeout(SAMPLE_MS + 20000, () => {
      req.destroy();
      done({ ok: false, note: "timeout" });
    });
  });
}

(async () => {
  console.log("probing for " + FILE);
  console.log("only responses declaring >= 150 MB are considered real\n");

  const results = [];
  for (const [label, url] of CANDIDATES) {
    const r = await probe(label, url);
    results.push(r);
    if (r.ok) {
      const eta = r.declared ? Math.round(r.declared / (r.kbps * 1024)) : null;
      console.log(
        `${label.padEnd(18)} ${String(r.kbps).padStart(6)} KB/s  declared=${(r.declared / 1048576).toFixed(1)} MiB` +
          (eta !== null ? `  ETA ${Math.floor(eta / 60)}m${eta % 60}s` : "") +
          (r.note ? "  (" + r.note + ")" : "")
      );
    } else {
      console.log(`${label.padEnd(18)} FAILED: ${r.note}`);
    }
  }

  const viable = results.filter((r) => r.ok && r.declared >= MIN_PLAUSIBLE_BYTES);
  console.log("");
  if (viable.length === 0) {
    console.log("No source declared a plausible SDK archive size.");
    process.exit(1);
  }
  const best = viable.sort((a, b) => b.kbps - a.kbps)[0];
  console.log("BEST: " + best.label + "  " + best.kbps + " KB/s");
  console.log("URL:  " + best.url);
})();

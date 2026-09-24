// Downloads the .NET 8 SDK using Node's own TLS stack.
//
// Why Node and not curl/PowerShell: this machine's schannel cannot acquire TLS
// credentials at all (AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS),
// so every schannel-based client (curl.exe, Invoke-WebRequest) fails on any
// HTTPS URL. Node links OpenSSL instead and works fine.
//
// Usage: node fetch-dotnet-sdk.js <destZipPath>

const fs = require("fs");
const path = require("path");
const https = require("https");

const dest = process.argv[2];
if (!dest) {
  console.error("usage: node fetch-dotnet-sdk.js <destZipPath>");
  process.exit(2);
}

function get(url, redirectsLeft = 5) {
  return new Promise((resolve, reject) => {
    if (redirectsLeft < 0) return reject(new Error("too many redirects"));
    https
      .get(url, { headers: { "User-Agent": "RecentDock-build-setup" } }, (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume();
          return resolve(get(res.headers.location, redirectsLeft - 1));
        }
        if (res.statusCode !== 200) {
          res.resume();
          return reject(new Error("HTTP " + res.statusCode + " for " + url));
        }
        resolve(res);
      })
      .on("error", reject);
  });
}

function getText(url) {
  return get(url).then(
    (res) =>
      new Promise((resolve, reject) => {
        let body = "";
        res.setEncoding("utf8");
        res.on("data", (c) => (body += c));
        res.on("end", () => resolve(body.trim()));
        res.on("error", reject);
      })
  );
}

(async () => {
  const channelUrl = "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/8.0/latest.version";
  const version = await getText(channelUrl);
  console.log("resolved SDK version: " + version);

  const url =
    "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/" +
    version +
    "/dotnet-sdk-" +
    version +
    "-win-x64.zip";
  console.log("downloading: " + url);

  fs.mkdirSync(path.dirname(dest), { recursive: true });

  const res = await get(url);
  const total = parseInt(res.headers["content-length"] || "0", 10);
  console.log("content-length: " + total + " bytes");

  let received = 0;
  let lastReport = 0;
  const out = fs.createWriteStream(dest);

  res.on("data", (chunk) => {
    received += chunk.length;
    const now = Date.now();
    if (now - lastReport > 5000) {
      lastReport = now;
      const pct = total ? ((received / total) * 100).toFixed(1) : "?";
      console.log("  " + pct + "%  " + (received / 1048576).toFixed(1) + " MiB");
    }
  });

  await new Promise((resolve, reject) => {
    res.pipe(out);
    out.on("finish", resolve);
    out.on("error", reject);
    res.on("error", reject);
  });

  const stat = fs.statSync(dest);
  console.log("done: " + dest + "  (" + (stat.size / 1048576).toFixed(1) + " MiB)");

  if (total && stat.size !== total) {
    console.error("SIZE MISMATCH: expected " + total + ", got " + stat.size);
    process.exit(1);
  }
})().catch((e) => {
  console.error("FAILED: " + e.message);
  process.exit(1);
});

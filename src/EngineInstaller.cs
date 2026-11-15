using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace BcnMiner
{
    // Downloads the mining engine from the official GitHub releases, the same way get-engine.ps1 does:
    //   - XMRig (latest release, Windows x64 or ARM64): xmrig.exe, WinRing0x64.sys, licence files
    //   - xmrig-cuda (optional, for NVIDIA): the build made with the newest CUDA toolkit (Blackwell needs 12.8+)
    // Every archive is checked against the SHA-256 GitHub publishes for it (the asset "digest", or the release's
    // SHA256SUMS). Nothing is installed when no hash is published or the hash differs. Files are copied into the
    // engine folder only after every download has been verified.
    public class EngineInstaller
    {
        public event Action<string, int> Progress;   // status text, percent 0..100 or -1 when unknown
        volatile bool cancel;
        readonly JavaScriptSerializer ser = new JavaScriptSerializer();

        public void Cancel() { cancel = true; }

        public class Result
        {
            public string XmrigVersion;
            public string CudaVersion;   // null when the plugin was not installed
            public string CudaToolkit;
        }

        public static bool IsArm64
        {
            get
            {
                return string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432"), "ARM64", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Lowest NVIDIA driver on this PC (0 = unknown): the plugin build is chosen so the driver can run it.
        public double NvidiaDriver;

        public Result Install(bool withCuda)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var tmp = Path.Combine(Path.GetTempPath(), "bcn-miner-engine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var result = new Result();
                var staged = new List<KeyValuePair<string, string>>(); // temp file -> name in engine folder

                // XMRig
                Report(L.F("eng.query", "XMRig"), -1);
                var rel = GetJson("https://api.github.com/repos/xmrig/xmrig/releases/latest");
                result.XmrigVersion = (J.S(rel, "tag_name") ?? "?").TrimStart('v');
                var assets = J.A(rel, "assets") ?? new object[0];
                string[] patterns = IsArm64
                    ? new[] { @"-windows-arm64\.zip$" }
                    : new[] { @"-windows-x64\.zip$", @"-msvc-win64\.zip$", @"-windows-gcc-x64\.zip$", @"-gcc-win64\.zip$" };
                object asset = null;
                foreach (var p in patterns)
                {
                    if (asset != null) break;
                    foreach (var a in assets) if (Regex.IsMatch(J.S(a, "name") ?? "", p)) { asset = a; break; }
                }
                if (asset == null) throw new InvalidOperationException(L.F("eng.no_asset", "xmrig " + result.XmrigVersion, IsArm64 ? "ARM64" : "x64"));
                var zip = DownloadVerified(asset, assets, tmp);
                Extract(zip, tmp, new[] { "xmrig.exe", "WinRing0x64.sys" }, new[] { "LICENSE" }, staged);
                if (!staged.Exists(kv => kv.Value.Equals("xmrig.exe", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("xmrig.exe not found in " + J.S(asset, "name"));

                // xmrig-cuda
                if (withCuda && !IsArm64)
                {
                    Report(L.F("eng.query", "xmrig-cuda"), -1);
                    var c = GetJson("https://api.github.com/repos/xmrig/xmrig-cuda/releases/latest");
                    var cassets = J.A(c, "assets") ?? new object[0];
                    // the newest CUDA build this driver can run (Blackwell needs 12.8+, old drivers need 10.2 / 11.x)
                    object best = null, newest = null;
                    Version bestVer = new Version(0, 0), newestVer = new Version(0, 0);
                    foreach (var a in cassets)
                    {
                        var m = Regex.Match(J.S(a, "name") ?? "", @"cuda(\d+)_(\d+)-win64\.zip$");
                        if (!m.Success) continue;
                        var v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                        if (v > newestVer) { newest = a; newestVer = v; }
                        bool fits = NvidiaDriver <= 0 || NvidiaDriver >= Gpu.DriverForCuda(v);
                        if (fits && v > bestVer) { best = a; bestVer = v; }
                    }
                    if (newest == null) throw new InvalidOperationException(L.F("eng.no_asset", "xmrig-cuda " + J.S(c, "tag_name"), "x64"));
                    if (best == null)
                        throw new InvalidOperationException(L.F("eng.old_driver", NvidiaDriver.ToString("0.00", CultureInfo.InvariantCulture)));
                    var czip = DownloadVerified(best, cassets, tmp);
                    Extract(czip, tmp, null, new[] { ".dll" }, staged);
                    result.CudaVersion = (J.S(c, "tag_name") ?? "?").TrimStart('v');
                    result.CudaToolkit = bestVer.ToString();
                }

                // everything verified: copy into place
                Check();
                Report(L.T("eng.installing"), -1);
                Directory.CreateDirectory(Engine.EngineDir);
                if (result.CudaVersion != null)
                    foreach (var old in Directory.GetFiles(Engine.EngineDir, "*.dll"))
                    {
                        var n = Path.GetFileName(old).ToLowerInvariant();
                        if (n == "xmrig-cuda.dll" || n.StartsWith("nvrtc")) File.Delete(old); // a previous plugin built for another CUDA
                    }
                foreach (var kv in staged)
                {
                    var dest = Path.Combine(Engine.EngineDir, kv.Value);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Copy(kv.Key, dest);
                }
                return result;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        void Report(string text, int pct)
        {
            if (Progress != null) Progress(text, pct);
        }

        void Check()
        {
            if (cancel) throw new OperationCanceledException();
        }

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "bcn-miner/" + App.Version;
            req.Accept = url.Contains("api.github.com") ? "application/vnd.github+json" : "*/*";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            return req;
        }

        Dictionary<string, object> GetJson(string url)
        {
            Check();
            using (var resp = Request(url).GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return ser.DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
        }

        string GetText(string url)
        {
            Check();
            using (var resp = Request(url).GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        // The hash GitHub publishes: the asset's own "digest" field, or the line for it in the release's SHA256SUMS.
        string ExpectedHash(object asset, object[] assets)
        {
            var name = J.S(asset, "name");
            var digest = J.S(asset, "digest") ?? "";
            var m = Regex.Match(digest, "^sha256:([0-9a-f]{64})$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            foreach (var a in assets)
            {
                if (J.S(a, "name") != "SHA256SUMS") continue;
                foreach (var line in GetText(J.S(a, "browser_download_url")).Split('\n'))
                {
                    var lm = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                    if (lm.Success && lm.Groups[2].Value.Trim() == name) return lm.Groups[1].Value.ToLowerInvariant();
                }
            }
            return null;
        }

        string DownloadVerified(object asset, object[] assets, string tmp)
        {
            string name = J.S(asset, "name");
            string expected = ExpectedHash(asset, assets);
            if (expected == null) throw new InvalidOperationException(L.F("eng.no_hash", name));
            string file = Path.Combine(tmp, name);
            long total = (long)J.N(asset, "size");
            using (var resp = Request(J.S(asset, "browser_download_url")).GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(file))
            using (var sha = SHA256.Create())
            {
                var buf = new byte[81920];
                long done = 0, lastReport = -1;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    Check();
                    dst.Write(buf, 0, n);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (done - lastReport > 256 * 1024 || done == total)
                    {
                        lastReport = done;
                        Report(L.F("eng.downloading", name, Mb(done), total > 0 ? Mb(total) : "?"), total > 0 ? (int)(done * 100 / total) : -1);
                    }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                Report(L.F("eng.verifying", name), 100);
                string actual = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new InvalidOperationException(L.F("eng.bad_hash", name, expected, actual));
            }
            return file;
        }

        static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // Extracts entries whose file name is in `names`, starts with one of `prefixesOrExt` or ends with it.
        void Extract(string zip, string tmp, string[] names, string[] prefixesOrExt, List<KeyValuePair<string, string>> staged)
        {
            Report(L.F("eng.extracting", Path.GetFileName(zip)), -1);
            var outDir = Path.Combine(tmp, Path.GetFileNameWithoutExtension(zip));
            Directory.CreateDirectory(outDir);
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var e in archive.Entries)
                {
                    Check();
                    var fn = e.Name;
                    if (fn.Length == 0) continue;
                    bool want = names != null && Array.Exists(names, x => x.Equals(fn, StringComparison.OrdinalIgnoreCase));
                    if (!want && prefixesOrExt != null)
                        foreach (var p in prefixesOrExt)
                            if (fn.StartsWith(p, StringComparison.OrdinalIgnoreCase) || fn.EndsWith(p, StringComparison.OrdinalIgnoreCase)) want = true;
                    if (!want) continue;
                    var path = Path.Combine(outDir, fn);
                    e.ExtractToFile(path, true);
                    staged.RemoveAll(kv => kv.Value.Equals(fn, StringComparison.OrdinalIgnoreCase));
                    staged.Add(new KeyValuePair<string, string>(path, fn));
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace BcnMiner
{
    public class DeviceStat
    {
        public string Key;      // "cpu", "cuda:0", "opencl:1"
        public string Type;     // cpu | cuda | opencl
        public int Index;
        public string Name;
        public double H10, H60, H15;
        public bool NoData = true;   // the engine has not reported a single hashrate value for this device yet
        public int Threads;
        public int Temp = -1, Power = -1, Fan = -1;
        public string FanUnit = "%";
    }

    public class EngineSnapshot
    {
        public bool Ok;
        public string Error;
        public string Version;
        public double H10, H60, H15m, Highest;
        public long Good, Total;
        public long Uptime;
        public long Diff;
        public string Pool;
        public long Ping;
        public bool Paused;
        public bool HugePages;
        public List<DeviceStat> Devices = new List<DeviceStat>();
        public long Rejected { get { return Math.Max(0, Total - Good); } }
    }

    // Runs XMRig (CPU + OpenCL + CUDA plugin) as a child process and reads its local HTTP API.
    // Algorithm: cn/0, the original CryptoNight that Bytecoin v3.5 (block v4) still uses.
    public class Engine
    {
        public static string EngineDir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine"); } }
        public static string XmrigPath { get { return Path.Combine(EngineDir, "xmrig.exe"); } }
        public static bool Installed { get { return File.Exists(XmrigPath); } }
        public static bool CudaPlugin { get { return File.Exists(Path.Combine(EngineDir, "xmrig-cuda.dll")); } }
        public static string ConfigPath { get { return Path.Combine(MinerSettings.Dir, "xmrig-config.json"); } }
        public static string LogPath { get { return Path.Combine(MinerSettings.Dir, "engine.log"); } }

        Process proc;
        int port;
        string token;
        IntPtr job = IntPtr.Zero;
        readonly JavaScriptSerializer ser = new JavaScriptSerializer();

        public int Generation { get; private set; }
        public long CurrentDiff { get; private set; }
        public DateTime StartedAt { get; private set; }
        public bool CudaOn { get; private set; }
        public bool OpenClOn { get; private set; }
        public string OpenClPlatform { get; private set; }
        public int CpuThreadCount { get; private set; }

        public event Action<string> Line;
        public event Action<int, int> Exited; // generation, exit code

        public bool Running
        {
            get { try { return proc != null && !proc.HasExited; } catch { return false; } }
        }

        public static string Version()
        {
            try { return Installed ? FileVersionInfo.GetVersionInfo(XmrigPath).ProductVersion : null; } catch { return null; }
        }

        // ---- configuration ---------------------------------------------------------------------------

        public static string DeviceList(MinerSettings s, string type)
        {
            // null = all devices of this type; "" = none; "0,2" = only these indices
            var all = new List<int>();
            foreach (var kd in s.KnownDevices)
            {
                var key = kd.Split('|')[0];
                if (!key.StartsWith(type + ":")) continue;
                int i;
                if (int.TryParse(key.Substring(type.Length + 1), out i) && !all.Contains(i)) all.Add(i);
            }
            if (all.Count == 0) return null;
            all.Sort();
            var en = new List<string>();
            foreach (var i in all)
                if (!s.DisabledDevices.Contains(type + ":" + i)) en.Add(i.ToString(CultureInfo.InvariantCulture));
            if (en.Count == all.Count) return null;
            return string.Join(",", en);
        }

        public static string ResolvePlatform(MinerSettings s, List<GpuInfo> gpus)
        {
            if (s.OpenClPlatform != "auto") return s.OpenClPlatform;
            foreach (var g in gpus) if (g.Vendor == "AMD" && !g.Integrated) return "AMD";
            foreach (var g in gpus) if (g.Vendor == "Intel" && !g.Integrated) return "Intel";
            return null; // no discrete OpenCL GPU: leave OpenCL off
        }

        public static bool HasNvidia(List<GpuInfo> gpus)
        {
            foreach (var g in gpus) if (g.Vendor == "NVIDIA") return true;
            return false;
        }

        // ---- cn/0 thread profiles ---------------------------------------------------------------------
        // XMRig's autoconfig deliberately disables cn/0 on every backend ("cn/0": false), and an alias
        // "cn/0": "cn" is only honoured when the "cn" profile already exists in the file. So the engine is
        // run once with autosave to let it tune the "cn" profile (cn/1: same 2 MB scratchpad and kernels)
        // for this CPU and these GPUs; the result is cached in probe.json and copied under "cn/0".

        public static string ProbePath { get { return Path.Combine(MinerSettings.Dir, "probe.json"); } }
        static string ProbeConfigPath { get { return Path.Combine(MinerSettings.Dir, "probe-config.json"); } }

        string ProbeSignature(MinerSettings s, List<GpuInfo> gpus, bool cuda, bool ocl, string platform)
        {
            var names = new List<string>();
            foreach (var g in gpus) names.Add(g.Name + "/" + g.Driver);
            return string.Join("|", new[] { Version() ?? "?", Environment.ProcessorCount.ToString(), cuda ? "cuda" : "", ocl ? "ocl:" + platform : "",
                CudaPlugin ? "plugin" : "", string.Join(",", names) });
        }

        public static void ForgetProbe()
        {
            try { File.Delete(ProbePath); } catch { }
        }

        Dictionary<string, object> Probe(MinerSettings s, List<GpuInfo> gpus, bool cuda, bool ocl, string platform)
        {
            string sig = ProbeSignature(s, gpus, cuda, ocl, platform);
            try
            {
                var cached = ser.DeserializeObject(File.ReadAllText(ProbePath, Encoding.UTF8)) as Dictionary<string, object>;
                if (cached != null && J.S(cached, "sig") == sig) return cached;
            }
            catch { }

            if (Line != null) Line("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "]  gui      " + L.T("e.probe"));
            var cfg = new Dictionary<string, object> {
                {"autosave", true}, {"background", false}, {"colors", false}, {"title", false}, {"donate-level", 1},
                {"http", new Dictionary<string, object> { {"enabled", false} }},
                {"cpu", new Dictionary<string, object> { {"enabled", true}, {"huge-pages", true}, {"max-threads-hint", 100}, {"asm", true} }},
                {"opencl", new Dictionary<string, object> { {"enabled", ocl}, {"cache", true}, {"platform", platform ?? "AMD"} }},
                {"cuda", new Dictionary<string, object> { {"enabled", cuda}, {"loader", "xmrig-cuda.dll"}, {"nvml", false} }},
                // the pool is never mined in the probe: cn/0 is still disabled there, the engine is stopped as soon as the file is written
                {"pools", new List<object> { new Dictionary<string, object> { {"algo", "cn/0"}, {"url", "127.0.0.1:9"}, {"user", "probe"}, {"pass", "x"} } }},
                {"pause-on-battery", false}
            };
            File.WriteAllText(ProbeConfigPath, ser.Serialize(cfg), new UTF8Encoding(false));

            var psi = new ProcessStartInfo(XmrigPath, "--config=\"" + ProbeConfigPath + "\" --no-color");
            psi.WorkingDirectory = EngineDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            Dictionary<string, object> found = null;
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (o, e) => { };
                p.ErrorDataReceived += (o, e) => { };
                p.Start();
                AttachToJob(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 30)
                {
                    System.Threading.Thread.Sleep(500);
                    try
                    {
                        var d = ser.DeserializeObject(File.ReadAllText(ProbeConfigPath, Encoding.UTF8)) as Dictionary<string, object>;
                        bool cpuOk = J.A(J.O(d, "cpu"), "cn") != null;
                        bool oclOk = !ocl || J.A(J.O(d, "opencl"), "cn") != null || sw.Elapsed.TotalSeconds > 20;
                        bool cudaOk = !cuda || J.A(J.O(d, "cuda"), "cn") != null || sw.Elapsed.TotalSeconds > 20;
                        if (cpuOk && oclOk && cudaOk) { found = d; break; }
                    }
                    catch { } // file is being rewritten
                    if (p.HasExited) break;
                }
                try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
            }
            var r = new Dictionary<string, object> { {"sig", sig},
                {"cpu", J.A(J.O(found, "cpu"), "cn")}, {"opencl", J.A(J.O(found, "opencl"), "cn")}, {"cuda", J.A(J.O(found, "cuda"), "cn")} };
            if (found != null)
            {
                try { File.WriteAllText(ProbePath, ser.Serialize(r), new UTF8Encoding(false)); } catch { }
            }
            if (Line != null)
            {
                string msg = found == null ? L.T("e.probe_fail")
                    : L.F("e.probe_ok", Count(r, "cpu"), Count(r, "opencl"), Count(r, "cuda"));
                Line("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "]  gui      " + msg);
            }
            return r;
        }

        static int Count(Dictionary<string, object> d, string k)
        {
            var a = J.A(d, k);
            return a == null ? 0 : a.Length;
        }

        // GPU thread entries carry the device "index"; drop the devices the user switched off.
        static List<object> FilterDevices(object[] threads, MinerSettings s, string type)
        {
            var r = new List<object>();
            if (threads == null) return r;
            foreach (var t in threads)
            {
                int idx = (int)J.N(t, "index");
                if (!s.DisabledDevices.Contains(type + ":" + idx)) r.Add(t);
            }
            return r;
        }

        static List<object> CpuThreads(object[] probed, int want)
        {
            var r = new List<object>();
            if (probed != null)
                foreach (var t in probed) { if (r.Count >= want) break; r.Add(t); }
            if (r.Count == 0)
                for (int i = 0; i < want; i++) r.Add(new object[] { 1, -1 });
            return r;
        }

        Dictionary<string, object> BuildConfig(MinerSettings s, List<GpuInfo> gpus, long diff, out string args)
        {
            string worker = MinerSettings.SafeWorker(s.Worker);
            string user = s.Mode == "solo" || string.IsNullOrWhiteSpace(s.Wallet) ? worker : s.Wallet.Trim();
            string pass = s.DiffMode == "off" ? "x" : "d=" + diff.ToString(CultureInfo.InvariantCulture);

            var pools = new List<object>();
            foreach (var url in s.ActivePools())
            {
                pools.Add(new Dictionary<string, object> {
                    {"algo", "cn/0"}, {"coin", null}, {"url", url}, {"user", user}, {"pass", pass}, {"rig-id", worker},
                    {"nicehash", false}, {"keepalive", true}, {"enabled", true}, {"tls", false}, {"daemon", false}
                });
            }

            int prio = s.Profile == "eco" ? 0 : (s.Profile == "max" ? 2 : 1); // XMRig: 0 idle, 1 below normal, 2 normal
            int bfactor = s.Profile == "eco" ? 10 : (s.Profile == "max" ? 2 : 6);
            int bsleep = s.Profile == "eco" ? 50 : 0;
            string platform = ResolvePlatform(s, gpus);
            bool wantCuda = s.CudaEnabled && CudaPlugin && HasNvidia(gpus);
            bool wantOcl = s.OpenClEnabled && platform != null;
            var probe = Probe(s, gpus, wantCuda, wantOcl, platform);
            var cpuThreads = CpuThreads(J.A(probe, "cpu"), s.EffectiveThreads());
            var cudaThreads = FilterDevices(J.A(probe, "cuda"), s, "cuda");
            var oclThreads = FilterDevices(J.A(probe, "opencl"), s, "opencl");
            CudaOn = wantCuda && cudaThreads.Count > 0;
            OpenClOn = wantOcl && oclThreads.Count > 0;
            OpenClPlatform = platform;
            CpuThreadCount = s.CpuEnabled ? cpuThreads.Count : 0;

            var cfg = new Dictionary<string, object> {
                {"api", new Dictionary<string, object> { {"id", null}, {"worker-id", worker} }},
                {"http", new Dictionary<string, object> { {"enabled", true}, {"host", "127.0.0.1"}, {"port", port},
                    {"access-token", token}, {"restricted", false} }},
                {"autosave", false},
                {"background", false},
                {"colors", false},
                {"title", false},
                {"cpu", new Dictionary<string, object> { {"enabled", s.CpuEnabled}, {"huge-pages", true}, {"hw-aes", null},
                    {"priority", prio}, {"memory-pool", false}, {"yield", s.Profile != "max"}, {"asm", true},
                    {"cn/0", cpuThreads} }},
                {"opencl", new Dictionary<string, object> { {"enabled", OpenClOn}, {"cache", true}, {"loader", null},
                    {"platform", platform ?? "AMD"}, {"adl", true}, {"cn/0", oclThreads} }},
                {"cuda", new Dictionary<string, object> { {"enabled", CudaOn}, {"loader", "xmrig-cuda.dll"}, {"nvml", true},
                    {"cn/0", ApplyCudaProfile(cudaThreads, bfactor, bsleep)} }},
                {"donate-level", 1},
                {"donate-over-proxy", 1},
                {"log-file", LogPath},
                {"pools", pools},
                {"print-time", 30},
                {"health-print-time", 60},
                {"retries", 5},
                {"retry-pause", 5},
                {"syslog", false},
                {"user-agent", "bcn-miner-gui/" + App.Version},
                {"verbose", 0},
                {"watch", false},
                {"pause-on-battery", s.PauseOnBattery},
                {"pause-on-active", false}
            };

            var a = new StringBuilder();
            a.Append("--config=\"").Append(ConfigPath).Append("\" --no-color");
            args = a.ToString();
            return cfg;
        }

        // The load profile sets how much GPU time CUDA kernels give back to the desktop.
        static List<object> ApplyCudaProfile(List<object> threads, int bfactor, int bsleep)
        {
            var r = new List<object>();
            foreach (var t in threads)
            {
                var d = t as Dictionary<string, object>;
                if (d == null) { r.Add(t); continue; }
                var c = new Dictionary<string, object>(d);
                c["bfactor"] = bfactor;
                c["bsleep"] = bsleep;
                r.Add(c);
            }
            return r;
        }

        // ---- process ---------------------------------------------------------------------------------

        public void Start(MinerSettings s, List<GpuInfo> gpus, long diff)
        {
            Stop();
            if (!Installed) throw new InvalidOperationException(L.F("e.not_found", XmrigPath));
            Directory.CreateDirectory(MinerSettings.Dir);
            port = FreePort();
            token = RandomToken();
            string args;
            var cfg = BuildConfig(s, gpus, diff, out args);
            File.WriteAllText(ConfigPath, ser.Serialize(cfg), new UTF8Encoding(false));
            try { if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 20 * 1024 * 1024) File.Delete(LogPath); } catch { }

            var psi = new ProcessStartInfo(XmrigPath, args);
            psi.WorkingDirectory = EngineDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            int gen = ++Generation;
            p.OutputDataReceived += (o, e) => { if (e.Data != null && Line != null) Line(e.Data); };
            p.ErrorDataReceived += (o, e) => { if (e.Data != null && Line != null) Line(e.Data); };
            p.Exited += (o, e) =>
            {
                int code = 0;
                try { code = p.ExitCode; } catch { }
                if (Exited != null) Exited(gen, code);
            };
            p.Start();
            AttachToJob(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            proc = p;
            CurrentDiff = diff;
            StartedAt = DateTime.Now;
        }

        public void Stop()
        {
            var p = proc;
            proc = null;
            if (p == null) return;
            Generation++; // the exit of this process is expected, ignore its Exited event
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch { }
            try { p.Dispose(); } catch { }
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        static string RandomToken()
        {
            var b = new byte[24];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        // ---- HTTP API --------------------------------------------------------------------------------

        object Call(string path, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Proxy = null;
            req.Timeout = 2000;
            req.ReadWriteTimeout = 2000;
            req.Headers["Authorization"] = "Bearer " + token;
            if (body != null)
            {
                req.Method = "POST";
                req.ContentType = "application/json";
                var b = Encoding.UTF8.GetBytes(body);
                req.ContentLength = b.Length;
                using (var st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            }
            using (var r = req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return ser.DeserializeObject(sr.ReadToEnd());
        }

        public bool Control(string method)
        {
            if (!Running) return false;
            try { Call("/json_rpc", "{\"method\":\"" + method + "\",\"id\":1,\"jsonrpc\":\"2.0\"}"); return true; }
            catch { return false; }
        }

        public EngineSnapshot Poll()
        {
            var s = new EngineSnapshot();
            if (!Running) { s.Error = "not running"; return s; }
            try
            {
                var sum = Call("/2/summary", null);
                s.Version = J.S(sum, "version");
                var hr = J.O(sum, "hashrate");
                var tot = J.A(hr, "total");
                s.H10 = J.At(tot, 0); s.H60 = J.At(tot, 1); s.H15m = J.At(tot, 2);
                s.Highest = J.N(hr, "highest");
                var res = J.O(sum, "results");
                s.Good = (long)J.N(res, "shares_good");
                s.Total = (long)J.N(res, "shares_total");
                var con = J.O(sum, "connection");
                s.Pool = J.S(con, "pool");
                s.Ping = (long)J.N(con, "ping");
                s.Diff = (long)J.N(con, "diff");
                s.Uptime = (long)J.N(sum, "uptime");
                s.Paused = J.B(sum, "paused");
                var hp = J.A(sum, "hugepages");
                s.HugePages = hp != null && hp.Length == 2 && J.N(hp[1]) > 0 && J.N(hp[0]) >= J.N(hp[1]);
                string cpuBrand = J.S(J.O(sum, "cpu"), "brand");

                var be = Call("/2/backends", null) as object[];
                if (be != null)
                {
                    foreach (var b in be)
                    {
                        string type = J.S(b, "type");
                        if (!J.B(b, "enabled") || type == null) continue;
                        var threads = J.A(b, "threads") ?? new object[0];
                        if (type == "cpu")
                        {
                            var d = new DeviceStat { Key = "cpu", Type = "cpu", Name = cpuBrand ?? "CPU", Threads = threads.Length };
                            var h = J.A(b, "hashrate");
                            d.H10 = J.At(h, 0); d.H60 = J.At(h, 1); d.H15 = J.At(h, 2);
                            d.NoData = !HasValue(h);
                            s.Devices.Add(d);
                            continue;
                        }
                        var byIndex = new Dictionary<int, DeviceStat>();
                        foreach (var t in threads)
                        {
                            int idx = (int)J.N(t, "index");
                            DeviceStat d;
                            if (!byIndex.TryGetValue(idx, out d))
                            {
                                d = new DeviceStat { Key = type + ":" + idx, Type = type, Index = idx };
                                d.Name = J.S(t, "board") ?? J.S(t, "name") ?? (type.ToUpperInvariant() + " #" + idx);
                                if (type == "cuda" && J.S(t, "name") != null) d.Name = J.S(t, "name");
                                byIndex[idx] = d;
                                s.Devices.Add(d);
                            }
                            d.Threads++;
                            var h = J.A(t, "hashrate");
                            d.H10 += J.At(h, 0); d.H60 += J.At(h, 1); d.H15 += J.At(h, 2);
                            if (HasValue(h)) d.NoData = false;
                            var health = J.O(t, "health");
                            if (health != null)
                            {
                                double temp = J.N(health, "temperature");
                                if (temp > 0) d.Temp = (int)temp;
                                double pw = J.N(health, "power");
                                if (pw > 0) d.Power = (int)pw;
                                var fans = J.A(health, "fan_speed");
                                if (fans != null && fans.Length > 0) { d.Fan = (int)J.N(fans[0]); d.FanUnit = "%"; }
                                else if (J.N(health, "rpm") > 0) { d.Fan = (int)J.N(health, "rpm"); d.FanUnit = " " + L.T("d.rpm"); }
                            }
                        }
                    }
                }
                // XMRig reports the total as null ("n/a") as soon as one thread has no data, e.g. a GPU that never
                // starts hashing. Show the sum of the devices that do report instead of zero.
                if (s.H10 <= 0 && s.H60 <= 0 && s.H15m <= 0)
                    foreach (var d in s.Devices) { s.H10 += d.H10; s.H60 += d.H60; s.H15m += d.H15; }
                s.Ok = true;
            }
            catch (Exception ex) { s.Error = ex.Message; }
            return s;
        }

        static bool HasValue(object[] a)
        {
            if (a == null) return false;
            foreach (var v in a) if (v != null) return true;
            return false;
        }

        // ---- kill the engine together with the GUI, even on a crash ----------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimit
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attr, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimit info, uint len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        void AttachToJob(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new ExtendedLimit();
                    info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf(typeof(ExtendedLimit)));
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace BcnMiner
{
    public class MinerSettings
    {
        public string Mode { get; set; }            // "pool" | "solo"
        public List<string> Pools { get; set; }     // host:port, first is primary, the rest are failover
        public string SoloBridge { get; set; }      // bcn-stratum bridge next to your own bytecoind
        public string Wallet { get; set; }
        public string Worker { get; set; }
        public string DiffMode { get; set; }        // "auto" | "fixed" | "off"
        public long FixedDiff { get; set; }
        public bool CpuEnabled { get; set; }
        public int CpuThreads { get; set; }         // 0 = half of logical CPUs
        public bool CudaEnabled { get; set; }
        public bool OpenClEnabled { get; set; }
        public string OpenClPlatform { get; set; }  // "auto" | "AMD" | "Intel" | "NVIDIA"
        public List<string> DisabledDevices { get; set; } // "cuda:0", "opencl:1"
        public List<string> KnownDevices { get; set; }    // "cuda:0|NVIDIA GeForce RTX 4070", learnt from the engine
        public List<string> GpuLoadCaps { get; set; }     // "cuda:0|192": max threads x blocks after the card hit a launch timeout
        public List<string> GpuLoadPercent { get; set; }  // "cuda:0|60": user-chosen load, % of the card's video memory (absent = default)
        public int GpuTempLimit { get; set; }
        public string Profile { get; set; }         // "eco" | "balanced" | "max"
        public string Theme { get; set; }           // "system" | "dark" | "light"
        public string Language { get; set; }        // "system" | one of L.Codes
        public bool StartWithWindows { get; set; }
        public bool AutoStartMining { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool PauseOnBattery { get; set; }
        public double LastHashrate { get; set; }

        // Open pool of the Bytecoin community: 0% fee, PPLNS, https://pool.bytecoin.community
        public const string CommunityPool = "pool.bytecoin.community:3333";

        public MinerSettings()
        {
            Mode = "pool";
            Pools = new List<string> { CommunityPool, "", "" };
            SoloBridge = "127.0.0.1:3333";
            Wallet = "";
            Worker = SafeWorker(Environment.MachineName);
            DiffMode = "auto";
            FixedDiff = 5000;
            CpuEnabled = true;
            CpuThreads = 0;
            CudaEnabled = true;
            OpenClEnabled = true;
            OpenClPlatform = "auto";
            DisabledDevices = new List<string>();
            KnownDevices = new List<string>();
            GpuLoadCaps = new List<string>();
            GpuLoadPercent = new List<string>();
            GpuTempLimit = 80;
            Profile = "balanced";
            Theme = "system";
            Language = "system";
            MinimizeToTray = true;
            PauseOnBattery = true;
        }

        public static string SafeWorker(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            var r = sb.ToString();
            if (r.Length > 32) r = r.Substring(0, 32);
            return r.Length == 0 ? "rig" : r;
        }

        // Portable mode: a "data" folder next to BCNMiner.exe holds settings, logs and the tuning cache,
        // so the whole program folder can be carried around. Without it, %APPDATA%\BCN Miner is used.
        static string dir;
        public static string Dir
        {
            get
            {
                if (dir == null)
                {
                    var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                    dir = Directory.Exists(local) && Writable(local) ? local
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BCN Miner");
                }
                return dir;
            }
        }

        public static bool Portable
        {
            get { return Dir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase); }
        }

        static bool Writable(string folder)
        {
            try
            {
                var probe = Path.Combine(folder, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public int LoadCap(string key)
        {
            foreach (var e in GpuLoadCaps)
            {
                var p = e.Split('|');
                int v;
                if (p.Length == 2 && p[0] == key && int.TryParse(p[1], out v)) return v;
            }
            return 0;
        }

        public void SetLoadCap(string key, int value)
        {
            GpuLoadCaps.RemoveAll(e => e.StartsWith(key + "|"));
            if (value > 0) GpuLoadCaps.Add(key + "|" + value);
        }

        // 0 = not set (use the card's default)
        public int LoadPercent(string key)
        {
            foreach (var e in GpuLoadPercent)
            {
                var p = e.Split('|');
                int v;
                if (p.Length == 2 && p[0] == key && int.TryParse(p[1], out v)) return v;
            }
            return 0;
        }

        public void SetLoadPercent(string key, int value)
        {
            GpuLoadPercent.RemoveAll(e => e.StartsWith(key + "|"));
            if (value > 0) GpuLoadPercent.Add(key + "|" + value);
        }

        public int EffectiveThreads()
        {
            int n = Environment.ProcessorCount;
            int t = CpuThreads <= 0 ? Math.Max(1, n / 2) : CpuThreads;
            return Math.Min(Math.Max(t, 1), n);
        }

        public List<string> ActivePools()
        {
            var r = new List<string>();
            if (Mode == "solo")
            {
                var b = Net.NormalizeHostPort(SoloBridge);
                if (b != null) r.Add(b);
                return r;
            }
            foreach (var p in Pools)
            {
                var n = Net.NormalizeHostPort(p);
                if (n != null && !r.Contains(n)) r.Add(n);
            }
            return r;
        }

        public static MinerSettings Load()
        {
            MinerSettings s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = new JavaScriptSerializer().Deserialize<MinerSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch { s = null; }
            if (s == null) s = new MinerSettings();
            s.Fix();
            return s;
        }

        void Fix()
        {
            var d = new MinerSettings();
            if (Mode != "solo") Mode = "pool";
            if (Pools == null) Pools = new List<string>();
            while (Pools.Count < 3) Pools.Add("");
            if (SoloBridge == null) SoloBridge = d.SoloBridge;
            if (Wallet == null) Wallet = "";
            if (string.IsNullOrEmpty(Worker)) Worker = d.Worker;
            if (DiffMode != "fixed" && DiffMode != "off") DiffMode = "auto";
            if (FixedDiff < 1) FixedDiff = d.FixedDiff;
            if (OpenClPlatform != "AMD" && OpenClPlatform != "Intel" && OpenClPlatform != "NVIDIA") OpenClPlatform = "auto";
            if (DisabledDevices == null) DisabledDevices = new List<string>();
            if (KnownDevices == null) KnownDevices = new List<string>();
            if (GpuLoadCaps == null) GpuLoadCaps = new List<string>();
            if (GpuLoadPercent == null) GpuLoadPercent = new List<string>();
            if (GpuTempLimit < 50 || GpuTempLimit > 95) GpuTempLimit = d.GpuTempLimit;
            if (Profile != "eco" && Profile != "max") Profile = "balanced";
            if (Theme != "dark" && Theme != "light") Theme = "system";
            if (Language == null || Array.IndexOf(L.Codes, Language) < 0) Language = "system";
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }
    }
}

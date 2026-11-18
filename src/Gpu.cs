using System;
using System.Collections.Generic;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BcnMiner
{
    public class GpuInfo
    {
        public string Name;
        public string Vendor;      // NVIDIA | AMD | Intel | other
        public string Backend;     // CUDA | OpenCL | —
        public string Arch;
        public string Driver;
        public long Vram;          // bytes, 0 = unknown
        public bool Integrated;
        public string Note;
    }

    // What Windows reports about the video adapters, before the engine runs.
    public static class Gpu
    {
        public static List<GpuInfo> Detect()
        {
            var list = new List<GpuInfo>();
            var vram = VramByName();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion, AdapterRAM FROM Win32_VideoController"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string pnp = Convert.ToString(o["PNPDeviceID"]) ?? "";
                        if (!pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue; // remote/basic display adapters
                        var g = new GpuInfo();
                        g.Name = (Convert.ToString(o["Name"]) ?? "GPU").Trim();
                        g.Driver = Convert.ToString(o["DriverVersion"]);
                        string up = pnp.ToUpperInvariant();
                        g.Vendor = up.Contains("VEN_10DE") ? "NVIDIA" : up.Contains("VEN_1002") ? "AMD" : up.Contains("VEN_8086") ? "Intel" : "other";
                        long v;
                        if (vram.TryGetValue(g.Name, out v)) g.Vram = v;
                        else { try { g.Vram = Convert.ToInt64(o["AdapterRAM"]); } catch { } }
                        Classify(g);
                        list.Add(g);
                    }
                }
            }
            catch { }
            return list;
        }

        static readonly Regex RxNum = new Regex(@"\brx\s*(\d{3,4})", RegexOptions.IgnoreCase);

        static void Classify(GpuInfo g)
        {
            string n = g.Name.ToLowerInvariant();
            if (g.Vendor == "NVIDIA")
            {
                g.Backend = "CUDA";
                if (Regex.IsMatch(n, @"rtx\s*50\d0|rtx pro \d+ blackwell|\bb\d00\b|gb\d0\d")) { g.Arch = "Blackwell"; g.Note = L.T("g.blackwell"); }
                else if (Regex.IsMatch(n, @"rtx\s*40\d0|ada generation|\bl40|\bl4\b")) g.Arch = "Ada Lovelace";
                else if (Regex.IsMatch(n, @"rtx\s*30\d0|rtx a\d{4}|\ba100\b|\ba10\b|\ba30\b|\ba40\b")) g.Arch = "Ampere";
                else if (Regex.IsMatch(n, @"rtx\s*20\d0|gtx\s*16\d0|titan rtx|quadro rtx|\bt4\b")) g.Arch = "Turing";
                else if (Regex.IsMatch(n, @"gtx\s*10\d0|titan x|titan xp|quadro p\d+|\bp\d{3}\b")) g.Arch = "Pascal";
                else if (Regex.IsMatch(n, @"gtx\s*9\d\d|gtx\s*750|quadro m\d+|\bm\d{2,4}m\b")) { g.Arch = "Maxwell"; g.Note = L.T("g.old_nv"); }
                else if (Regex.IsMatch(n, @"gtx\s*7\d\d|gtx\s*6\d\d")) { g.Arch = "Kepler"; g.Note = L.T("g.old_nv"); }
                else g.Arch = "NVIDIA";
                if (n.Contains("mx") && Regex.IsMatch(n, @"\bmx\s*\d{3}\b")) { g.Note = L.T("g.mx"); }
            }
            else if (g.Vendor == "AMD")
            {
                g.Backend = "OpenCL";
                var m = RxNum.Match(n);
                int num = 0;
                if (m.Success) int.TryParse(m.Groups[1].Value, out num);
                if (num >= 9000) g.Arch = "RDNA 4";
                else if (num >= 7000) g.Arch = "RDNA 3";
                else if (num >= 6000) g.Arch = "RDNA 2";
                else if (num >= 5000) g.Arch = "RDNA 1";
                else if (num >= 400 && num < 600) g.Arch = "GCN 4 (Polaris)";
                else if (n.Contains("vega") && !n.Contains("graphics")) g.Arch = "GCN 5 (Vega)";
                else if (n.Contains("radeon vii")) g.Arch = "GCN 5 (Vega 20)";
                else if (n.Contains("radeon pro w7") || n.Contains("radeon pro w6")) g.Arch = n.Contains("w7") ? "RDNA 3" : "RDNA 2";
                else if (n.Contains("instinct")) g.Arch = "CDNA";
                else if (n.Contains("graphics") || n.Contains("radeon(tm)") || n.Contains("vega 8") || n.Contains("vega 11") || Regex.IsMatch(n, @"radeon \d{3}m"))
                {
                    g.Arch = L.T("g.apu"); g.Integrated = true;
                }
                else g.Arch = "AMD";
            }
            else if (g.Vendor == "Intel")
            {
                bool discreteArc = Regex.IsMatch(n, @"arc\(tm\)\s*[ab]\d{3}|arc\s*[ab]\d{3}|arc pro [ab]\d+");
                if (discreteArc)
                {
                    g.Backend = "OpenCL";
                    g.Arch = Regex.IsMatch(n, @"\bb\d{3}") ? "Xe2 (Battlemage)" : "Xe-HPG (Alchemist)";
                    g.Note = L.T("g.arc");
                }
                else
                {
                    g.Backend = "—"; g.Integrated = true;
                    g.Arch = n.Contains("arc") ? L.T("g.igpu_arc") : L.T("g.igpu");
                }
            }
            else { g.Backend = "—"; g.Arch = L.T("g.unknown"); }

            if (g.Integrated && g.Note == null)
                g.Note = L.T("g.integrated");
        }

        // Real VRAM size: Win32_VideoController.AdapterRAM is a 32-bit field and caps at 4 GB.
        static Dictionary<string, long> VramByName()
        {
            var r = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return r;
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                var desc = k.GetValue("DriverDesc") as string;
                                if (desc == null) continue;
                                object q = k.GetValue("HardwareInformation.qwMemorySize");
                                long size = 0;
                                if (q is long) size = (long)q;
                                else if (q is byte[] && ((byte[])q).Length >= 8) size = BitConverter.ToInt64((byte[])q, 0);
                                else
                                {
                                    object d = k.GetValue("HardwareInformation.MemorySize");
                                    if (d is int) size = (uint)(int)d;
                                    else if (d is byte[] && ((byte[])d).Length >= 4) size = BitConverter.ToUInt32((byte[])d, 0);
                                }
                                if (size > 0 && !r.ContainsKey(desc.Trim())) r[desc.Trim()] = size;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return r;
        }

        // NVIDIA driver version from the Windows one: 27.21.14.5167 -> 451.67 (last digit of the 3rd part + the 4th part).
        public static double NvidiaDriver(string windowsVersion)
        {
            try
            {
                var p = (windowsVersion ?? "").Split('.');
                if (p.Length < 4) return 0;
                var s = p[2].Substring(p[2].Length - 1) + p[3].PadLeft(4, '0');
                return int.Parse(s) / 100.0;
            }
            catch { return 0; }
        }

        // Lowest NVIDIA driver among the cards (0 = unknown / no NVIDIA): it decides which CUDA build of the plugin can run.
        public static double MinNvidiaDriver(List<GpuInfo> gpus)
        {
            double min = 0;
            foreach (var g in gpus)
            {
                if (g.Vendor != "NVIDIA") continue;
                double d = NvidiaDriver(g.Driver);
                if (d > 0 && (min == 0 || d < min)) min = d;
            }
            return min;
        }

        // Old cards get a cautious default load: Maxwell / Kepler, 4 GB of video memory or less, or an NVIDIA
        // driver older than CUDA 11.1's (452.39). A GTX 965M on driver 451 hung at XMRig's own 70% load.
        public static bool IsOld(GpuInfo g)
        {
            if (g == null) return false;
            var a = (g.Arch ?? "").ToLowerInvariant();
            if (a.Contains("maxwell") || a.Contains("kepler")) return true;
            if (g.Vram > 0 && g.Vram <= 4L * 1073741824) return true;
            if (g.Vendor == "NVIDIA") { double d = NvidiaDriver(g.Driver); if (d > 0 && d < 452.39) return true; }
            return false;
        }

        public const int OldLoadPercent = 40, NewLoadPercent = 80;

        public static int DefaultLoadPercent(GpuInfo g) { return IsOld(g) ? OldLoadPercent : NewLoadPercent; }

        // Minimum Windows driver for a CUDA toolkit (NVIDIA's minor-version compatibility table).
        public static double DriverForCuda(Version cuda)
        {
            if (cuda.Major >= 13) return 580.0;
            if (cuda.Major == 12) return 527.41;
            if (cuda.Major == 11) return 452.39;
            if (cuda.Major == 10 && cuda.Minor >= 2) return 441.22;
            if (cuda.Major == 10) return 411.31;
            return 0;
        }

        public static string CpuName()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    var s = k == null ? null : k.GetValue("ProcessorNameString") as string;
                    if (!string.IsNullOrEmpty(s)) return Regex.Replace(s.Trim(), @"\s+", " ");
                }
            }
            catch { }
            return "CPU";
        }

        // For matching names the engine reports ("NVIDIA GeForce RTX 4070") with Windows names.
        public static string Key(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant().Replace("(tm)", "").Replace("(r)", "").Replace("nvidia", "").Replace("amd", "").Replace("intel", ""))
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        public static GpuInfo Match(List<GpuInfo> gpus, string engineName)
        {
            var k = Key(engineName);
            if (k.Length == 0) return null;
            foreach (var g in gpus)
            {
                var gk = Key(g.Name);
                if (gk.Length > 0 && (gk == k || gk.Contains(k) || k.Contains(gk))) return g;
            }
            return null;
        }

        public static string Size(long bytes)
        {
            if (bytes <= 0) return "?";
            double gb = bytes / 1073741824.0;
            return gb >= 1 ? Math.Round(gb, gb >= 10 ? 0 : 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + L.T("g.gb")
                           : Math.Round(bytes / 1048576.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + L.T("g.mb");
        }
    }
}

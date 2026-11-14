using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace BcnMiner
{
    // Defensive navigation over JavaScriptSerializer output (Dictionary<string,object> / object[]).
    public static class J
    {
        public static object V(object o, string k)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(k, out v)) return v;
            return null;
        }
        public static Dictionary<string, object> O(object o, string k) { return V(o, k) as Dictionary<string, object>; }
        public static object[] A(object o, string k) { return V(o, k) as object[]; }
        public static double N(object v)
        {
            if (v == null || v is bool || v is string && ((string)v).Length == 0) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        public static double N(object o, string k) { return N(V(o, k)); }
        public static double At(object[] a, int i) { return a != null && i < a.Length ? N(a[i]) : 0; }
        public static string S(object o, string k)
        {
            var v = V(o, k);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static bool B(object o, string k)
        {
            var v = V(o, k);
            return v is bool && (bool)v;
        }
    }

    public static class Fmt
    {
        static readonly NumberFormatInfo Nfi = MakeNfi();
        static NumberFormatInfo MakeNfi()
        {
            var n = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            n.NumberGroupSeparator = " ";
            n.NumberDecimalSeparator = ".";
            return n;
        }

        public static string Int(double v) { return Math.Round(v).ToString("#,0", Nfi); }

        public static double UnitOf(double h) { return h >= 1e6 ? 1e6 : (h >= 1e3 ? 1e3 : 1); }
        public static string UnitName(double unit) { return unit >= 1e6 ? "MH/s" : (unit >= 1e3 ? "kH/s" : "H/s"); }

        public static string Num(double v, double unit)
        {
            double x = v / unit;
            if (unit == 1) return x >= 100 ? x.ToString("0", Nfi) : x.ToString("0.0", Nfi);
            return x >= 100 ? x.ToString("0.0", Nfi) : x.ToString("0.00", Nfi);
        }

        public static string Hash(double h)
        {
            double u = UnitOf(h);
            return Num(h, u) + " " + UnitName(u);
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return L.F("dur.s", (int)t.TotalSeconds);
            if (t.TotalHours < 1) return L.F("dur.m", (int)t.TotalMinutes);
            if (t.TotalDays < 1) return L.F("dur.hm", (int)t.TotalHours, t.Minutes);
            return L.F("dur.dh", (int)t.TotalDays, t.Hours);
        }
    }

    public static class Net
    {
        static readonly Regex HostPort = new Regex(@"^(?:stratum\+tcp://|tcp://)?([A-Za-z0-9.\-]+|\[[0-9A-Fa-f:]+\]):(\d{1,5})/?$");

        // -> "host:port" or null.
        public static string NormalizeHostPort(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = HostPort.Match(s.Trim());
            if (!m.Success) return null;
            int port;
            if (!int.TryParse(m.Groups[2].Value, out port) || port < 1 || port > 65535) return null;
            return m.Groups[1].Value + ":" + port;
        }

        static readonly Regex Base58 = new Regex("^[1-9A-HJ-NP-Za-km-z]+$");

        // Bytecoin addresses: legacy "2..." (95 chars) and Amethyst "bcnZ..." (longer); both base58.
        public static bool LooksLikeAddress(string a)
        {
            if (string.IsNullOrEmpty(a)) return false;
            a = a.Trim();
            return a.Length >= 90 && a.Length <= 200 && Base58.IsMatch(a) && (a[0] == '2' || a.StartsWith("bcn"));
        }

        // Stratum target -> share difficulty (Monero-style 32- or 64-bit little-endian target).
        public static long TargetToDiff(string hex)
        {
            try
            {
                if (hex == null) return 0;
                if (hex.Length == 8)
                {
                    uint t = BitConverter.ToUInt32(FromHex(hex), 0);
                    return t == 0 ? 0 : (long)(0xFFFFFFFFUL / t);
                }
                if (hex.Length == 16)
                {
                    ulong t = BitConverter.ToUInt64(FromHex(hex), 0);
                    return t == 0 ? 0 : (long)(ulong.MaxValue / t);
                }
            }
            catch { }
            return 0;
        }

        static byte[] FromHex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }

        // One stratum login round trip, the same request the engine sends. -> (ok, human readable answer)
        public static bool TestStratum(string hostport, string login, out string answer)
        {
            var hp = NormalizeHostPort(hostport);
            if (hp == null) { answer = L.T("net.bad_addr"); return false; }
            int colon = hp.LastIndexOf(':');
            string host = hp.Substring(0, colon).Trim('[', ']');
            int port = int.Parse(hp.Substring(colon + 1));
            var sw = Stopwatch.StartNew();
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) { answer = L.F("net.timeout", hp); return false; }
                    c.EndConnect(ar);
                    long connectMs = sw.ElapsedMilliseconds;
                    var st = c.GetStream();
                    st.ReadTimeout = 10000;
                    var ser = new JavaScriptSerializer();
                    var req = new Dictionary<string, object> {
                        {"id", 1}, {"jsonrpc", "2.0"}, {"method", "login"},
                        {"params", new Dictionary<string, object> { {"login", string.IsNullOrEmpty(login) ? "test" : login}, {"pass", "x"},
                            {"agent", "bcn-miner-gui/" + App.Version}, {"algo", new[] {"cn/0"}} } }
                    };
                    var bytes = Encoding.UTF8.GetBytes(ser.Serialize(req) + "\n");
                    st.Write(bytes, 0, bytes.Length);
                    var line = new StreamReader(st, Encoding.UTF8).ReadLine();
                    if (line == null) { answer = L.T("net.closed"); return false; }
                    var resp = ser.DeserializeObject(line);
                    var err = J.V(resp, "error");
                    if (err != null)
                    {
                        string msg = J.S(err, "message") ?? Convert.ToString(err);
                        if (msg == "node not synced") msg += " " + L.T("net.not_synced");
                        answer = L.F("net.pool_error", msg);
                        return false;
                    }
                    var job = J.O(J.V(resp, "result"), "job");
                    if (job == null) { answer = L.T("net.no_job"); return false; }
                    var parts = new List<string> { L.T("net.ok") };
                    double h = J.N(job, "height");
                    if (h > 0) parts.Add(L.F("net.block", Fmt.Int(h)));
                    parts.Add(L.F("net.algo", J.S(job, "algo") ?? L.T("net.algo_none")));
                    long d = TargetToDiff(J.S(job, "target"));
                    if (d > 0) parts.Add(L.F("net.share_diff", Fmt.Int(d)));
                    parts.Add(L.F("net.connect_ms", connectMs));
                    answer = string.Join(" · ", parts);
                    return true;
                }
            }
            catch (Exception ex)
            {
                answer = L.F("net.fail", hp, ex.Message);
                return false;
            }
        }
    }
}

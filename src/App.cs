using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

[assembly: AssemblyTitle("BCN Miner")]
[assembly: AssemblyProduct("BCN Miner")]
[assembly: AssemblyDescription("GUI miner for Bytecoin (BCN), CryptoNight cn/0, CPU + NVIDIA CUDA + AMD/Intel OpenCL")]
[assembly: AssemblyCompany("Bytecoin Community")]
[assembly: AssemblyCopyright("Bytecoin Community, LGPL-3.0")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]

namespace BcnMiner
{
    public static class App
    {
        public const string Version = "1.1.0";
        public const string SiteUrl = "https://bytecoin.community";
        public const string PoolUrl = "https://pool.bytecoin.community";
        public const string RepoUrl = "https://github.com/bytecoinBCN-community/bcn-miner";

        [STAThread]
        public static int Main(string[] args)
        {
            bool created;
            var settings = MinerSettings.Load();
            L.Set(settings.Language);
            using (var mutex = new Mutex(true, "Local\\BcnMinerGui-v1", out created))
            {
                if (!created)
                {
                    MessageBox.Show(L.T("app.running"), "BCN Miner", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    WriteCrash(e.Exception);
                    MessageBox.Show(L.F("app.crash", e.Exception.Message, CrashPath), "BCN Miner", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => WriteCrash(e.ExceptionObject as Exception);

                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("BcnMiner.Styles.xaml"))
                    app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(st));

                Theme.Apply(settings.Theme);
                bool minimized = Array.IndexOf(args, "--minimized") >= 0;
                var w = new MainWindow(settings, minimized);
                if (!minimized) w.Show();
                return app.Run();
            }
        }

        static string CrashPath { get { return Path.Combine(MinerSettings.Dir, "crash.log"); } }

        static void WriteCrash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(MinerSettings.Dir);
                File.AppendAllText(CrashPath, DateTime.Now.ToString("u") + " " + ex + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}

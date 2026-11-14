using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Ellipse = System.Windows.Shapes.Ellipse;
using Shape = System.Windows.Shapes.Shape;
using Path = System.IO.Path;

namespace BcnMiner
{
    public class MainWindow : Window
    {
        enum St { Stopped, Starting, Running, Paused }

        readonly MinerSettings S;
        readonly Engine engine = new Engine();
        readonly DispatcherTimer timer;
        List<GpuInfo> gpus;
        St state = St.Stopped;
        bool userPaused, tempPaused, polling, exiting, trayHintShown, pendingRestart, suppress;
        DateTime runStart, lastSample = DateTime.MinValue, lastDiffCheck;
        int restartTries, startTicket, currentPage;
        long height;
        EngineSnapshot last;
        readonly List<string> logLines = new List<string>();
        static readonly Regex RxHeight = new Regex(@"height (\d+)");
        static readonly Regex RxStamp = new Regex(@"^\[\d{4}-\d\d-\d\d (\d\d:\d\d:\d\d)\.\d+\]\s+(.*)$");

        // chrome + nav
        Border rootBorder;
        Button maxBtn;
        RadioButton[] navs;
        FrameworkElement[] pages;
        Ellipse navDot;
        TextBlock navState, navAlgo, navHeight;

        // mining page
        U.Pill pill;
        Button btnMain, btnPause, btnStop;
        Border banner;
        TextBlock bannerText;
        Button bannerBtn;
        Action bannerAction;
        Run hashVal, hashUnit, shareGood, shareBad, diffVal, tempVal, tempUnit;
        TextBlock hashSub, shareSub, diffSub, tempSub, chartUnit;
        HashChart chart;
        RadioButton[] modeBtns;
        TextBlock addrLabel, walletLabel, walletHint;
        TextBox addrBox, walletBox;
        Slider thrSlider;
        TextBlock thrLabel;
        StackPanel miniLog;

        // pool page
        TextBox[] poolBoxes;
        TextBox soloBox, workerBox, fixedDiffBox;
        RadioButton[] diffBtns;
        TextBlock testResult;
        Button testBtn;

        // devices page
        TextBlock engineStatus, backendNote;
        CheckBox cbCpu, cbCuda, cbOcl;
        RadioButton[] platBtns;
        Slider tempSlider;
        TextBlock tempLimitLabel;
        StackPanel devPanel;
        readonly Dictionary<string, TextBlock> devStats = new Dictionary<string, TextBlock>();

        // log page
        TextBox logBox;
        int logBoxLines;

        // settings page
        RadioButton[] themeBtns, profBtns, langBtns;
        TextBlock profDesc;

        System.Windows.Forms.NotifyIcon tray;
        System.Windows.Forms.ToolStripMenuItem trayShow, trayPause, trayExit;

        public MainWindow(MinerSettings settings, bool minimized)
        {
            S = settings;
            Title = "BCN Miner";
            var wa = SystemParameters.WorkArea;
            MinWidth = Math.Min(1000, wa.Width); MinHeight = Math.Min(620, wa.Height);
            Width = Math.Max(MinWidth, Math.Min(1280, wa.Width * 0.94));
            Height = Math.Max(MinHeight, Math.Min(800, wa.Height * 0.94));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
            FontSize = 13;
            SetResourceReference(BackgroundProperty, "Bg");
            SetResourceReference(ForegroundProperty, "Text");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 38,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            SetLangAttr();

            gpus = Gpu.Detect();
            Content = BuildRoot();

            engine.Line += l => Dispatcher.BeginInvoke(new Action(() => OnEngineLine(l)));
            engine.Exited += (gen, code) => Dispatcher.BeginInvoke(new Action(() => OnEngineExited(gen, code)));

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (a, b) => Tick();
            timer.Start();

            StateChanged += (a, b) => SyncMaximized();
            SourceInitialized += (a, b) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    int round = 2; // DWMWCP_ROUND, Windows 11
                    DwmSetWindowAttribute(hwnd, 33, ref round, 4);
                }
                catch { }
            };
            Closing += OnClosing;
            SystemEvents.UserPreferenceChanged += OnSystemTheme;

            SetupTray();
            Log("gui", L.F("log.hello", App.Version, Engine.Installed ? "XMRig " + (Engine.Version() ?? "?") : L.T("log.engine_none")));
            foreach (var g in gpus)
                Log("gui", L.F("log.adapter", g.Name + " · " + g.Vendor + " · " + g.Arch + " · " + Gpu.Size(g.Vram) + " · " + g.Backend));
            UpdateBanner();
            Refresh();

            if (S.AutoStartMining)
                Dispatcher.BeginInvoke(new Action(() => StartMining()), DispatcherPriority.ApplicationIdle);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void SetLangAttr()
        {
            try { Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Code == "zh" ? "zh-CN" : L.Code); } catch { }
        }

        void SyncMaximized()
        {
            rootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            maxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        // Switching language rebuilds the whole UI; mining state, log and chart history are kept.
        void ApplyLanguage(string setting)
        {
            S.Language = setting;
            S.Save();
            L.Set(setting);
            SetLangAttr();
            var history = chart != null ? new List<double>(chart.Data) : new List<double>();
            int page = currentPage;
            gpus = Gpu.Detect();
            Content = BuildRoot();
            chart.Data.AddRange(history);
            navs[page].IsChecked = true;
            SyncMaximized();
            UpdateTrayTexts();
            UpdateBanner();
            Refresh();
            if (last != null) UpdateDeviceStats(last);
        }

        // =============================================================================================
        // Layout
        // =============================================================================================

        UIElement BuildRoot()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // title bar (Windows: caption buttons on the right)
            var title = new Grid();
            title.SetResourceReference(Panel.BackgroundProperty, "Title");
            var brand = U.H(8, U.Logo(16, true), U.T("BCN Miner", 12, "Muted", true));
            ((TextBlock)brand.Children[1]).VerticalAlignment = VerticalAlignment.Center;
            brand.Margin = new Thickness(14, 0, 0, 0);
            brand.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(brand);
            var caps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var minBtn = new Button { Style = U.St("Cap"), Content = "\uE921", ToolTip = L.T("cap.min") };
            maxBtn = new Button { Style = U.St("Cap"), Content = "\uE922", ToolTip = L.T("cap.max") };
            var closeBtn = new Button { Style = U.St("CapClose"), Content = "\uE8BB", ToolTip = L.T("cap.close") };
            AutomationProperties.SetName(minBtn, L.T("cap.min")); AutomationProperties.SetName(maxBtn, L.T("cap.max")); AutomationProperties.SetName(closeBtn, L.T("cap.close"));
            minBtn.Click += (a, b) => WindowState = WindowState.Minimized;
            maxBtn.Click += (a, b) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            closeBtn.Click += (a, b) => Close();
            foreach (var c in new[] { minBtn, maxBtn, closeBtn })
            {
                WindowChrome.SetIsHitTestVisibleInChrome(c, true);
                caps.Children.Add(c);
            }
            title.Children.Add(caps);
            root.Children.Add(title);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            body.Children.Add(BuildNav());

            var host = new Grid();
            Grid.SetColumn(host, 1);
            pages = new FrameworkElement[] { BuildMining(), BuildPool(), BuildDevices(), BuildLog(), BuildSettings() };
            foreach (var p in pages) { p.Visibility = Visibility.Collapsed; host.Children.Add(p); }
            pages[0].Visibility = Visibility.Visible;
            currentPage = 0;
            body.Children.Add(host);

            rootBorder = new Border { BorderThickness = new Thickness(1), Child = root };
            rootBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
            return rootBorder;
        }

        UIElement BuildNav()
        {
            var nav = new DockPanel { LastChildFill = false };
            var border = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(12, 20, 12, 16), Child = nav };
            border.SetResourceReference(Border.BackgroundProperty, "Nav");
            border.SetResourceReference(Border.BorderBrushProperty, "Line");

            var top = new StackPanel();
            DockPanel.SetDock(top, Dock.Top);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(U.T("BCN Miner", 15, "Text", true));
            names.Children.Add(U.Link("bytecoin.community", App.SiteUrl, 11));
            var brand = U.H(10, U.Logo(32), names);
            brand.Margin = new Thickness(10, 0, 10, 20);
            top.Children.Add(brand);

            string[] labels = { L.T("nav.mining"), L.T("nav.pool"), L.T("nav.devices"), L.T("nav.log"), L.T("nav.settings") };
            string[] icons = { U.IcMining, U.IcPool, U.IcChip, U.IcLog, U.IcSettings };
            navs = new RadioButton[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var tb = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var r = new RadioButton { Style = U.St("NavItem"), GroupName = "nav", Content = U.H(12, U.Icon(icons[i]), tb), IsChecked = i == 0 };
                AutomationProperties.SetName(r, labels[i]);
                r.Checked += (a, b) => ShowPage(idx);
                navs[i] = r;
                top.Children.Add(r);
            }
            nav.Children.Add(top);

            var status = new StackPanel { Margin = new Thickness(0) };
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 14, 10, 0), Child = status };
            line.SetResourceReference(Border.BorderBrushProperty, "Line");
            DockPanel.SetDock(line, Dock.Bottom);
            navDot = U.Dot("Muted");
            navState = U.T(L.T("st.stopped"), 13, "Muted", true);
            status.Children.Add(U.H(8, navDot, navState));
            navAlgo = U.T("CryptoNight · cn/0", 12, "Muted"); navAlgo.FontFamily = U.Mono; navAlgo.Margin = new Thickness(0, 6, 0, 0);
            navHeight = U.T(L.T("nav.block_none"), 12, "Muted"); navHeight.FontFamily = U.Mono; navHeight.Margin = new Thickness(0, 6, 0, 0);
            status.Children.Add(navAlgo);
            status.Children.Add(navHeight);
            nav.Children.Add(line);
            return border;
        }

        void ShowPage(int idx)
        {
            if (pages == null) return;
            currentPage = idx;
            for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
            if (idx == 3 && logBox != null) logBox.ScrollToEnd();
            if (idx == 2) RebuildDevices();
        }

        void Go(int idx) { navs[idx].IsChecked = true; }

        static StackPanel Page()
        {
            return new StackPanel { Margin = new Thickness(32, 28, 32, 28) };
        }

        // ---------------------------------------------------------------- Mining
        FrameworkElement BuildMining()
        {
            var sp = Page();

            pill = U.MakePill();
            btnMain = U.Btn(L.T("m.start"), "Primary", (a, b) => OnMain(), U.IcPlay);
            btnPause = U.Btn(L.T("m.pause"), "Secondary", (a, b) => PauseMining(), U.IcPause);
            btnStop = U.Btn(L.T("m.stop"), "Secondary", (a, b) => StopMining(), U.IcStop);
            var head = U.Columns("1*", U.H1(L.T("nav.mining")), "auto", pill.Root, "auto", btnPause, "auto", btnStop, "auto", btnMain);
            pill.Root.Margin = new Thickness(12, 0, 0, 0);
            btnPause.Margin = new Thickness(12, 0, 0, 0);
            btnStop.Margin = new Thickness(12, 0, 0, 0);
            btnMain.Margin = new Thickness(12, 0, 0, 0);
            sp.Children.Add(head);

            bannerText = U.T("", 13, "Text");
            bannerText.VerticalAlignment = VerticalAlignment.Center;
            bannerBtn = U.Btn("", "Small", (a, b) => { if (bannerAction != null) bannerAction(); });
            bannerBtn.Margin = new Thickness(16, 0, 0, 0);
            banner = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed, Child = U.Columns("1*", bannerText, "auto", bannerBtn) };
            banner.SetResourceReference(Border.BackgroundProperty, "WarnBg");
            banner.SetResourceReference(Border.BorderBrushProperty, "Warn");
            sp.Children.Add(banner);

            var stats = new UniformGrid { Columns = 4, Rows = 1, Margin = new Thickness(-7, 18, -7, 0) };
            Run dummy;
            stats.Children.Add(Stat(L.T("m.hashrate"), out hashVal, out hashUnit, out hashSub));
            stats.Children.Add(Stat(L.T("m.shares"), out shareGood, out shareBad, out shareSub));
            stats.Children.Add(Stat(L.T("m.diff"), out diffVal, out dummy, out diffSub));
            stats.Children.Add(Stat(L.T("m.temp"), out tempVal, out tempUnit, out tempSub));
            sp.Children.Add(stats);

            chart = new HashChart { Height = 190, Margin = new Thickness(0, 10, 0, 0) };
            chartUnit = U.T("kH/s", 12, "Muted");
            var chartCard = U.Card(U.V(0, U.Columns("1*", U.H2(L.T("m.chart")), "auto", chartUnit), chart));

            var conn = new StackPanel();
            conn.Children.Add(U.H2(L.T("m.connection")));
            var seg = U.Segmented("mode", new[] { L.T("m.mode_pool"), L.T("m.mode_solo") }, new[] { "pool", "solo" }, S.Mode, v => { S.Mode = v; SyncConnection(); Changed(); }, out modeBtns);
            seg.Margin = new Thickness(0, 12, 0, 0);
            conn.Children.Add(seg);
            addrBox = U.Field("", L.T("m.pool_addr"));
            addrBox.TextChanged += (a, b) =>
            {
                if (suppress) return;
                if (S.Mode == "solo") S.SoloBridge = addrBox.Text.Trim(); else S.Pools[0] = addrBox.Text.Trim();
                if (poolBoxes != null) { suppress = true; poolBoxes[0].Text = S.Pools[0]; soloBox.Text = S.SoloBridge; suppress = false; }
                Changed();
            };
            var addrBlock = U.Labeled(L.T("m.pool_addr"), addrBox);
            addrLabel = (TextBlock)addrBlock.Children[0];
            addrBlock.Margin = new Thickness(0, 12, 0, 0);
            conn.Children.Add(addrBlock);
            walletBox = U.Field("", L.T("m.wallet"));
            walletBox.TextChanged += (a, b) =>
            {
                if (suppress) return;
                if (S.Mode == "solo") S.Worker = walletBox.Text.Trim(); else S.Wallet = walletBox.Text.Trim();
                if (workerBox != null) { suppress = true; workerBox.Text = S.Worker; suppress = false; }
                Changed();
            };
            var walletBlock = U.Labeled(L.T("m.wallet"), walletBox, " ");
            walletLabel = (TextBlock)walletBlock.Children[0];
            walletHint = (TextBlock)walletBlock.Children[2];
            walletBlock.Margin = new Thickness(0, 12, 0, 0);
            conn.Children.Add(walletBlock);

            thrLabel = U.T("", 12, "Text", true);
            var thrHead = U.Columns("1*", U.T(L.T("m.cpu_threads"), 12, "Muted"), "auto", thrLabel);
            thrHead.Margin = new Thickness(0, 14, 0, 0);
            conn.Children.Add(thrHead);
            thrSlider = U.Range(1, Environment.ProcessorCount, S.EffectiveThreads());
            AutomationProperties.SetName(thrSlider, L.T("m.cpu_threads"));
            thrSlider.Margin = new Thickness(0, 6, 0, 0);
            thrSlider.ValueChanged += (a, b) =>
            {
                if (suppress) return;
                S.CpuThreads = (int)thrSlider.Value;
                UpdateThreadsLabel();
                Changed();
            };
            conn.Children.Add(thrSlider);
            var connCard = U.Card(conn);

            var row = U.Columns("1.55*", chartCard, 14, null, "1*", connCard);
            row.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(row);

            miniLog = new StackPanel();
            var logCard = U.Card(miniLog, "LogBg");
            logCard.Padding = new Thickness(16, 12, 16, 12);
            logCard.Margin = new Thickness(0, 14, 0, 0);
            logCard.MinHeight = 110;
            logCard.Cursor = Cursors.Hand;
            logCard.ToolTip = L.T("m.log_tip");
            logCard.MouseLeftButtonUp += (a, b) => Go(3);
            sp.Children.Add(logCard);

            SyncConnection();
            UpdateThreadsLabel();
            RefreshMiniLog();
            return U.Scroll(sp);
        }

        Border Stat(string label, out Run val, out Run unit, out TextBlock sub)
        {
            var tb = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
            Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            val = new Run("—");
            unit = new Run("") { FontSize = 14, FontWeight = FontWeights.Normal };
            unit.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            tb.Inlines.Add(val);
            tb.Inlines.Add(unit);
            sub = U.T(" ", 11, "Muted");
            sub.Margin = new Thickness(0, 2, 0, 0);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sub.TextWrapping = TextWrapping.NoWrap;
            var lt = U.T(label, 12, "Muted");
            lt.TextTrimming = TextTrimming.CharacterEllipsis;
            lt.TextWrapping = TextWrapping.NoWrap;
            lt.ToolTip = label;
            var card = U.Card(U.V(0, lt, tb, sub));
            card.Margin = new Thickness(7, 0, 7, 0);
            return card;
        }

        void SyncConnection()
        {
            suppress = true;
            bool solo = S.Mode == "solo";
            addrLabel.Text = solo ? L.T("m.bridge_addr") : L.T("m.pool_addr");
            AutomationProperties.SetName(addrBox, addrLabel.Text);
            addrBox.Text = solo ? S.SoloBridge : S.Pools[0];
            walletLabel.Text = solo ? L.T("m.worker") : L.T("m.wallet");
            AutomationProperties.SetName(walletBox, walletLabel.Text);
            walletBox.Text = solo ? S.Worker : S.Wallet;
            walletHint.Text = solo ? L.T("m.hint_solo") : L.T("m.hint_pool");
            U.SelectSeg(modeBtns, S.Mode);
            suppress = false;
        }

        void UpdateThreadsLabel()
        {
            if (!S.CpuEnabled) { thrLabel.Text = L.T("m.cpu_off"); thrSlider.IsEnabled = false; return; }
            thrSlider.IsEnabled = true;
            thrLabel.Text = S.EffectiveThreads() + " / " + Environment.ProcessorCount;
        }

        // ---------------------------------------------------------------- Pool & node
        FrameworkElement BuildPool()
        {
            var sp = Page();
            sp.Children.Add(U.H1(L.T("nav.pool")));

            var pools = new StackPanel();
            pools.Children.Add(U.H2(L.T("p.pools")));
            var pi = U.T(L.T("p.pools_hint"), 12, "Muted");
            pi.Margin = new Thickness(0, 6, 0, 0);
            pools.Children.Add(pi);
            poolBoxes = new TextBox[3];
            string[] pl = { L.T("p.primary"), L.T("p.backup1"), L.T("p.backup2") };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var tb = U.Field(S.Pools[i], L.F("p.pool_n", pl[i]));
                tb.TextChanged += (a, b) =>
                {
                    if (suppress) return;
                    S.Pools[idx] = tb.Text.Trim();
                    if (idx == 0 && S.Mode == "pool") { suppress = true; addrBox.Text = S.Pools[0]; suppress = false; }
                    Changed();
                };
                poolBoxes[i] = tb;
                var l = U.Labeled(pl[i], tb);
                l.Margin = new Thickness(0, 12, 0, 0);
                pools.Children.Add(l);
            }
            var community = U.Btn(L.T("p.community"), "Small", (a, b) =>
            {
                poolBoxes[0].Text = MinerSettings.CommunityPool;  // TextChanged saves it and updates the mining page
            }, U.IcPool);
            var poolPage = U.Btn(L.T("p.pool_page"), "Small", (a, b) => U.OpenUrl(App.PoolUrl), U.IcGlobe);
            poolPage.ToolTip = App.PoolUrl;
            community.Margin = new Thickness(0, 12, 10, 0);
            poolPage.Margin = new Thickness(0, 12, 10, 0);
            var poolBtns = new WrapPanel();
            poolBtns.Children.Add(community);
            poolBtns.Children.Add(poolPage);
            pools.Children.Add(poolBtns);
            var poolsCard = U.Card(pools);

            var solo = new StackPanel();
            solo.Children.Add(U.H2(L.T("p.solo")));
            var si = U.T(L.T("p.solo_hint"), 12, "Muted");
            si.Margin = new Thickness(0, 6, 0, 0);
            solo.Children.Add(si);
            soloBox = U.Field(S.SoloBridge, L.T("p.bridge_label"));
            soloBox.TextChanged += (a, b) =>
            {
                if (suppress) return;
                S.SoloBridge = soloBox.Text.Trim();
                if (S.Mode == "solo") { suppress = true; addrBox.Text = S.SoloBridge; suppress = false; }
                Changed();
            };
            var sl = U.Labeled(L.T("p.bridge_label"), soloBox, L.T("p.bridge_hint"));
            sl.Margin = new Thickness(0, 12, 0, 0);
            solo.Children.Add(sl);
            var soloCard = U.Card(solo);

            var row = U.Columns("1*", poolsCard, 14, null, "1*", soloCard);
            row.Margin = new Thickness(0, 18, 0, 0);
            sp.Children.Add(row);

            var w = new StackPanel();
            w.Children.Add(U.H2(L.T("p.worker_diff")));
            workerBox = U.Field(S.Worker, L.T("p.worker_label"));
            workerBox.TextChanged += (a, b) =>
            {
                if (suppress) return;
                S.Worker = workerBox.Text.Trim();
                if (S.Mode == "solo") { suppress = true; walletBox.Text = S.Worker; suppress = false; }
                Changed();
            };
            var wl = U.Labeled(L.T("p.worker_label"), workerBox, L.T("p.worker_hint"));
            wl.Margin = new Thickness(0, 12, 0, 0);
            w.Children.Add(wl);
            var dl = U.T(L.T("m.diff"), 12, "Muted");
            dl.Margin = new Thickness(0, 14, 0, 5);
            w.Children.Add(dl);
            var dseg = U.Segmented("diff", new[] { L.T("p.diff_auto"), L.T("p.diff_fixed"), L.T("p.diff_off") }, new[] { "auto", "fixed", "off" }, S.DiffMode,
                v => { S.DiffMode = v; fixedDiffBox.IsEnabled = v == "fixed"; Changed(); }, out diffBtns);
            w.Children.Add(dseg);
            fixedDiffBox = U.Field(S.FixedDiff.ToString(), L.T("p.fixed_value"));
            fixedDiffBox.IsEnabled = S.DiffMode == "fixed";
            fixedDiffBox.TextChanged += (a, b) =>
            {
                long v;
                if (long.TryParse(fixedDiffBox.Text.Trim(), out v) && v >= 1) { S.FixedDiff = v; Changed(); }
            };
            var fl = U.Labeled(L.T("p.fixed_value"), fixedDiffBox, L.T("p.diff_hint"));
            fl.Margin = new Thickness(0, 12, 0, 0);
            w.Children.Add(fl);
            var wCard = U.Card(w);

            var t = new StackPanel();
            t.Children.Add(U.H2(L.T("p.test")));
            var ti = U.T(L.T("p.test_hint"), 12, "Muted");
            ti.Margin = new Thickness(0, 6, 0, 0);
            t.Children.Add(ti);
            testBtn = U.Btn(L.T("p.test_btn"), "Secondary", (a, b) => TestConnection(), U.IcRefresh);
            testBtn.HorizontalAlignment = HorizontalAlignment.Left;
            testBtn.Margin = new Thickness(0, 14, 0, 0);
            t.Children.Add(testBtn);
            testResult = U.T("", 13, "Muted");
            testResult.Margin = new Thickness(0, 12, 0, 0);
            t.Children.Add(testResult);
            var tCard = U.Card(t);

            var row2 = U.Columns("1*", wCard, 14, null, "1*", tCard);
            row2.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(row2);
            return U.Scroll(sp);
        }

        void TestConnection()
        {
            var pools = S.ActivePools();
            if (pools.Count == 0) { SetTest(false, S.Mode == "solo" ? L.T("p.need_bridge") : L.T("p.need_pool")); return; }
            string target = pools[0];
            string login = S.Mode == "solo" || string.IsNullOrWhiteSpace(S.Wallet) ? MinerSettings.SafeWorker(S.Worker) : S.Wallet.Trim();
            testBtn.IsEnabled = false;
            SetTest(null, L.F("p.connecting", target));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string answer;
                bool ok = Net.TestStratum(target, login, out answer);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (testBtn == null) return;
                    testBtn.IsEnabled = true;
                    SetTest(ok, target + ": " + answer);
                    Log("gui", L.F("log.test", target, answer));
                }));
            });
        }

        void SetTest(bool? ok, string text)
        {
            testResult.Text = text;
            testResult.SetResourceReference(TextBlock.ForegroundProperty, ok == null ? "Muted" : (ok.Value ? "Good" : "Bad"));
        }

        // ---------------------------------------------------------------- Devices
        FrameworkElement BuildDevices()
        {
            var sp = Page();
            sp.Children.Add(U.H1(L.T("nav.devices")));

            var eng = new StackPanel();
            eng.Children.Add(U.H2(L.T("d.engine")));
            engineStatus = U.T("", 13, "Muted");
            engineStatus.Margin = new Thickness(0, 8, 0, 0);
            eng.Children.Add(engineStatus);
            var engBtns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var b1 = U.Btn(L.T("d.open_engine"), "Small", (a, b) => OpenFolder(Engine.EngineDir), U.IcFolder);
            var b2 = U.Btn(L.T("d.recheck"), "Small", (a, b) => { gpus = Gpu.Detect(); Engine.ForgetProbe(); UpdateEngineStatus(); RebuildDevices(); UpdateBanner(); }, U.IcRefresh);
            b1.Margin = new Thickness(0, 6, 10, 0);
            b2.Margin = new Thickness(0, 6, 10, 0);
            engBtns.Children.Add(b1);
            engBtns.Children.Add(b2);
            eng.Children.Add(engBtns);
            var engCard = U.Card(eng);

            var be = new StackPanel();
            be.Children.Add(U.H2(L.T("d.backends")));
            cbCpu = U.Switch(L.T("d.cpu"), L.T("d.cpu_hint"), S.CpuEnabled, v => { S.CpuEnabled = v; UpdateThreadsLabel(); Changed(); });
            cbCpu.Margin = new Thickness(0, 12, 0, 0);
            be.Children.Add(cbCpu);
            cbCuda = U.Switch("NVIDIA · CUDA", L.T("d.cuda_hint"), S.CudaEnabled, v => { S.CudaEnabled = v; Changed(); });
            cbCuda.Margin = new Thickness(0, 12, 0, 0);
            be.Children.Add(cbCuda);
            cbOcl = U.Switch("AMD / Intel Arc · OpenCL", L.T("d.ocl_hint"), S.OpenClEnabled, v => { S.OpenClEnabled = v; Changed(); });
            cbOcl.Margin = new Thickness(0, 12, 0, 0);
            be.Children.Add(cbOcl);
            var pl = U.T(L.T("d.platform"), 12, "Muted");
            pl.Margin = new Thickness(0, 14, 0, 5);
            be.Children.Add(pl);
            be.Children.Add(U.Segmented("plat", new[] { L.T("p.diff_auto"), "AMD", "Intel", "NVIDIA" }, new[] { "auto", "AMD", "Intel", "NVIDIA" }, S.OpenClPlatform,
                v => { S.OpenClPlatform = v; UpdateEngineStatus(); Changed(); }, out platBtns));
            tempLimitLabel = U.T("", 12, "Text", true);
            var th = U.Columns("1*", U.T(L.T("d.temp_pause"), 12, "Muted"), "auto", tempLimitLabel);
            th.Margin = new Thickness(0, 16, 0, 0);
            be.Children.Add(th);
            tempSlider = U.Range(60, 90, S.GpuTempLimit);
            AutomationProperties.SetName(tempSlider, L.T("d.temp_pause"));
            tempSlider.Margin = new Thickness(0, 6, 0, 0);
            tempSlider.ValueChanged += (a, b) => { S.GpuTempLimit = (int)tempSlider.Value; tempLimitLabel.Text = S.GpuTempLimit + " °C"; S.Save(); };
            tempLimitLabel.Text = S.GpuTempLimit + " °C";
            be.Children.Add(tempSlider);
            var tl = U.T(L.T("d.temp_hint"), 11, "Muted");
            tl.Margin = new Thickness(0, 6, 0, 0);
            be.Children.Add(tl);
            backendNote = U.T("", 12, "Muted");
            backendNote.Margin = new Thickness(0, 12, 0, 0);
            be.Children.Add(backendNote);
            var beCard = U.Card(be);

            var row = U.Columns("1*", engCard, 14, null, "1*", beCard);
            row.Margin = new Thickness(0, 18, 0, 0);
            sp.Children.Add(row);

            devPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            sp.Children.Add(devPanel);
            UpdateEngineStatus();
            RebuildDevices();
            return U.Scroll(sp);
        }

        void UpdateEngineStatus()
        {
            if (engineStatus == null) return;
            if (!Engine.Installed)
            {
                engineStatus.Text = L.T("d.engine_missing");
                engineStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            }
            else
            {
                engineStatus.Text = "XMRig " + (Engine.Version() ?? "?") + " · engine\\xmrig.exe\n" +
                    (Engine.CudaPlugin ? L.T("d.cuda_ok") : (Engine.HasNvidia(gpus) ? L.T("d.cuda_missing") : L.T("d.cuda_unneeded"))) +
                    "\n" + L.T("d.algo_note");
                engineStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            }
            if (backendNote != null)
            {
                var p = Engine.ResolvePlatform(S, gpus);
                backendNote.Text = S.OpenClEnabled ? (p == null ? L.T("d.ocl_none") : L.F("d.ocl_platform", p)) : L.T("d.ocl_off");
            }
        }

        void RebuildDevices()
        {
            if (devPanel == null) return;
            devPanel.Children.Clear();
            devStats.Clear();
            devPanel.Children.Add(U.H2(L.T("d.found")));

            AddDeviceCard("cpu", Gpu.CpuName(), new[] { "CPU", L.F("d.logical", Environment.ProcessorCount) }, L.T("d.cpu_note"), null);

            var shown = new HashSet<string>();
            foreach (var kd in S.KnownDevices)
            {
                var parts = kd.Split(new[] { '|' }, 2);
                string key = parts[0], name = parts.Length > 1 ? parts[1] : key;
                var g = Gpu.Match(gpus, name);
                if (g != null) shown.Add(g.Name);
                string type = key.StartsWith("cuda") ? "CUDA" : "OpenCL";
                var tags = new List<string> { type + " #" + key.Substring(key.IndexOf(':') + 1) };
                if (g != null) { tags.Add(g.Arch); if (g.Vram > 0) tags.Add(Gpu.Size(g.Vram)); }
                AddDeviceCard(key, name, tags.ToArray(), g != null ? g.Note : null, key, g != null ? g.Driver : null);
            }
            foreach (var g in gpus)
            {
                if (shown.Contains(g.Name)) continue;
                var tags = new List<string> { g.Vendor, g.Arch };
                if (g.Vram > 0) tags.Add(Gpu.Size(g.Vram));
                tags.Add(g.Backend == "—" ? L.T("d.unused") : g.Backend);
                string note = g.Note;
                if (!g.Integrated && g.Backend != "—")
                    note = (note == null ? "" : note + " ") + L.T("d.toggle_later");
                if (g.Vendor == "NVIDIA" && !Engine.CudaPlugin) note = L.T("d.no_plugin") + " " + (g.Note ?? "");
                AddDeviceCard("wmi:" + g.Name, g.Name, tags.ToArray(), note, null, g.Driver);
            }
            if (gpus.Count == 0 && S.KnownDevices.Count == 0)
            {
                var t = U.T(L.T("d.none"), 13, "Muted");
                t.Margin = new Thickness(0, 10, 0, 0);
                devPanel.Children.Add(t);
            }
            if (last != null) UpdateDeviceStats(last);
        }

        void AddDeviceCard(string key, string name, string[] tags, string note, string toggleKey, string driver = null)
        {
            var left = new StackPanel();
            left.Children.Add(U.T(name, 14, "Text", true));
            var tagRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var t in tags)
            {
                if (string.IsNullOrEmpty(t)) continue;
                var b = U.Badge(t);
                b.Margin = new Thickness(0, 0, 6, 4);
                tagRow.Children.Add(b);
            }
            if (!string.IsNullOrEmpty(driver)) { var b = U.Badge(L.F("d.driver", driver)); b.Margin = new Thickness(0, 0, 6, 4); tagRow.Children.Add(b); }
            left.Children.Add(tagRow);
            if (!string.IsNullOrEmpty(note)) { var n = U.T(note, 12, "Muted"); n.Margin = new Thickness(0, 4, 0, 0); left.Children.Add(n); }
            var stat = U.T(state == St.Stopped ? L.T("d.not_running") : L.T("d.waiting"), 13, "Muted");
            stat.FontFamily = U.Mono;
            stat.Margin = new Thickness(0, 8, 0, 0);
            left.Children.Add(stat);
            devStats[key] = stat;

            UIElement right = null;
            if (toggleKey != null)
            {
                var cb = new CheckBox { Style = U.St("Switch"), IsChecked = !S.DisabledDevices.Contains(toggleKey), Content = U.T(L.T("d.mine"), 13, "Muted"), VerticalAlignment = VerticalAlignment.Top };
                AutomationProperties.SetName(cb, L.F("d.mine_on", name));
                cb.Checked += (a, b) => { S.DisabledDevices.Remove(toggleKey); Changed(); };
                cb.Unchecked += (a, b) => { if (!S.DisabledDevices.Contains(toggleKey)) S.DisabledDevices.Add(toggleKey); Changed(); };
                right = cb;
            }
            var card = U.Card(U.Columns("1*", left, "auto", right));
            card.Margin = new Thickness(0, 10, 0, 0);
            devPanel.Children.Add(card);
        }

        void UpdateDeviceStats(EngineSnapshot s)
        {
            foreach (var kv in devStats)
            {
                DeviceStat d = null;
                if (s != null && s.Ok) foreach (var x in s.Devices) if (x.Key == kv.Key) d = x;
                if (state == St.Stopped || s == null || !s.Ok) { kv.Value.Text = kv.Key.StartsWith("wmi:") ? "" : L.T("d.not_running"); continue; }
                if (d == null)
                {
                    kv.Value.Text = kv.Key == "cpu" ? (S.CpuEnabled ? L.T("m.waiting") : L.T("d.off")) :
                        (S.DisabledDevices.Contains(kv.Key) ? L.T("d.off") : (kv.Key.StartsWith("wmi:") ? "" : L.T("d.not_used_engine")));
                    continue;
                }
                var parts = new List<string> { Fmt.Hash(d.H10 > 0 ? d.H10 : d.H60) };
                if (d.Type == "cpu") parts.Add(L.F("d.threads", d.Threads));
                if (d.Temp > 0) parts.Add(d.Temp + " °C");
                if (d.Power > 0) parts.Add(L.F("d.watts", d.Power));
                if (d.Fan > 0) parts.Add(L.F("d.fan", d.Fan + d.FanUnit));
                kv.Value.Text = string.Join(" · ", parts);
            }
        }

        // ---------------------------------------------------------------- Log
        FrameworkElement BuildLog()
        {
            var g = new Grid { Margin = new Thickness(32, 28, 32, 28) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var bCopy = U.Btn(L.T("l.copy"), "Small", (a, b) => { try { Clipboard.SetText(string.Join(Environment.NewLine, logLines)); } catch { } }, U.IcCopy);
            var bClear = U.Btn(L.T("l.clear"), "Small", (a, b) => { logLines.Clear(); logBox.Clear(); logBoxLines = 0; RefreshMiniLog(); });
            var bFile = U.Btn(L.T("l.file"), "Small", (a, b) => { if (File.Exists(Engine.LogPath)) OpenFile(Engine.LogPath); else OpenFolder(MinerSettings.Dir); }, U.IcFolder);
            bClear.Margin = new Thickness(10, 0, 0, 0);
            bFile.Margin = new Thickness(10, 0, 0, 0);
            var head = U.Columns("1*", U.H1(L.T("nav.log")), "auto", bCopy, "auto", bClear, "auto", bFile);
            g.Children.Add(head);
            logBox = new TextBox { Style = U.St("Area"), Margin = new Thickness(0, 18, 0, 0), FontSize = 12 };
            AutomationProperties.SetName(logBox, L.T("l.name"));
            Grid.SetRow(logBox, 1);
            g.Children.Add(logBox);
            logBoxLines = 0;
            int from = Math.Max(0, logLines.Count - 2000);
            if (logLines.Count > 0) logBox.Text = string.Join("\n", logLines.GetRange(from, logLines.Count - from)) + "\n";
            logBoxLines = logLines.Count - from;
            return g;
        }

        // ---------------------------------------------------------------- Settings
        FrameworkElement BuildSettings()
        {
            var sp = Page();
            sp.Children.Add(U.H1(L.T("nav.settings")));

            var look = new StackPanel();
            look.Children.Add(U.H2(L.T("s.look")));
            var ll = U.T(L.T("s.language"), 12, "Muted");
            ll.Margin = new Thickness(0, 12, 0, 5);
            look.Children.Add(ll);
            var langLabels = new List<string> { L.T("s.system") };
            var langValues = new List<string> { "system" };
            for (int i = 0; i < L.Codes.Length; i++) { langLabels.Add(L.Names[i]); langValues.Add(L.Codes[i]); }
            look.Children.Add(U.Segmented("lang", langLabels.ToArray(), langValues.ToArray(), S.Language,
                v => { if (v != S.Language) Dispatcher.BeginInvoke(new Action(() => ApplyLanguage(v))); }, out langBtns, 3));

            var tl = U.T(L.T("s.theme"), 12, "Muted");
            tl.Margin = new Thickness(0, 18, 0, 5);
            look.Children.Add(tl);
            look.Children.Add(U.Segmented("theme", new[] { L.T("s.system"), L.T("s.dark"), L.T("s.light") }, new[] { "system", "dark", "light" }, S.Theme,
                v => { S.Theme = v; Theme.Apply(v); chart.InvalidateVisual(); S.Save(); }, out themeBtns));

            var pl = U.T(L.T("s.profile"), 12, "Muted");
            pl.Margin = new Thickness(0, 18, 0, 5);
            look.Children.Add(pl);
            look.Children.Add(U.Segmented("prof", new[] { L.T("s.eco"), L.T("s.balanced"), L.T("s.max") }, new[] { "eco", "balanced", "max" }, S.Profile,
                v => { S.Profile = v; UpdateProfileDesc(); Changed(); }, out profBtns));
            profDesc = U.T("", 12, "Muted");
            profDesc.Margin = new Thickness(0, 8, 0, 0);
            look.Children.Add(profDesc);
            UpdateProfileDesc();
            var lookCard = U.Card(look);

            var run = new StackPanel();
            run.Children.Add(U.H2(L.T("s.run")));
            var c1 = U.Switch(L.T("s.autorun"), L.T("s.autorun_hint"), S.StartWithWindows, v => { S.StartWithWindows = v; SetAutorun(v); S.Save(); });
            var c2 = U.Switch(L.T("s.automine"), null, S.AutoStartMining, v => { S.AutoStartMining = v; S.Save(); });
            var c3 = U.Switch(L.T("s.tray"), L.T("s.tray_hint"), S.MinimizeToTray, v => { S.MinimizeToTray = v; S.Save(); });
            var c4 = U.Switch(L.T("s.battery"), null, S.PauseOnBattery, v => { S.PauseOnBattery = v; Changed(); });
            foreach (var c in new[] { c1, c2, c3, c4 }) { c.Margin = new Thickness(0, 14, 0, 0); run.Children.Add(c); }
            var runCard = U.Card(run);

            var row = U.Columns("1*", lookCard, 14, null, "1*", runCard);
            row.Margin = new Thickness(0, 18, 0, 0);
            sp.Children.Add(row);

            var about = new StackPanel();
            about.Children.Add(U.H2(L.T("s.about")));
            var at = U.T(L.F("s.about_text", App.Version, MinerSettings.Dir) + "\n" + (MinerSettings.Portable ? L.T("s.portable_on") : L.T("s.portable_off")), 12, "Muted");
            at.Margin = new Thickness(0, 8, 0, 0);
            about.Children.Add(at);
            var ab = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var o1 = U.Btn(L.T("s.open_settings"), "Small", (a, b) => OpenFolder(MinerSettings.Dir), U.IcFolder);
            var o2 = U.Btn(L.T("s.open_app"), "Small", (a, b) => OpenFolder(AppDomain.CurrentDomain.BaseDirectory), U.IcFolder);
            var w1 = U.Btn("bytecoin.community", "Small", (a, b) => U.OpenUrl(App.SiteUrl), U.IcGlobe);
            var w2 = U.Btn("pool.bytecoin.community", "Small", (a, b) => U.OpenUrl(App.PoolUrl), U.IcGlobe);
            var w3 = U.Btn("GitHub", "Small", (a, b) => U.OpenUrl(App.RepoUrl), U.IcCode);
            w1.ToolTip = App.SiteUrl; w2.ToolTip = App.PoolUrl; w3.ToolTip = App.RepoUrl;
            foreach (var x in new[] { w1, w2, w3, o1, o2 }) { x.Margin = new Thickness(0, 6, 10, 0); ab.Children.Add(x); }
            about.Children.Add(ab);
            var aboutCard = U.Card(about);
            aboutCard.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(aboutCard);
            return U.Scroll(sp);
        }

        void UpdateProfileDesc()
        {
            profDesc.Text = S.Profile == "eco" ? L.T("s.eco_desc") : S.Profile == "max" ? L.T("s.max_desc") : L.T("s.balanced_desc");
        }

        // =============================================================================================
        // Mining control
        // =============================================================================================

        void Changed()
        {
            S.Save();
            if (state != St.Stopped && !pendingRestart)
            {
                pendingRestart = true;
                UpdateBanner();
            }
        }

        string Validate(out bool fatal)
        {
            fatal = true;
            if (!Engine.Installed) return L.T("v.no_engine");
            if (S.ActivePools().Count == 0) return S.Mode == "solo" ? L.T("v.no_bridge") : L.T("v.no_pool");
            if (S.Mode == "pool" && string.IsNullOrWhiteSpace(S.Wallet)) return L.T("v.no_wallet");
            bool any = S.CpuEnabled || (S.CudaEnabled && Engine.CudaPlugin && Engine.HasNvidia(gpus)) || (S.OpenClEnabled && Engine.ResolvePlatform(S, gpus) != null);
            if (!any) return L.T("v.no_backend");
            fatal = false;
            if (S.Mode == "pool" && !Net.LooksLikeAddress(S.Wallet)) return L.T("v.bad_wallet");
            return null;
        }

        long StartDiff()
        {
            if (S.DiffMode == "fixed") return S.FixedDiff;
            return AutoDiff(S.LastHashrate);
        }

        static long AutoDiff(double h)
        {
            return Math.Max(1000, (long)Math.Round(h * 30 / 100.0) * 100);
        }

        void OnMain()
        {
            if (state == St.Stopped) StartMining();
            else if (state == St.Paused) ResumeMining();
        }

        void StartMining(bool silent = false)
        {
            bool fatal;
            var msg = Validate(out fatal);
            if (fatal)
            {
                ShowBanner(msg, Engine.Installed ? L.T("b.configure") : L.T("nav.devices"), () => Go(Engine.Installed ? (S.Mode == "solo" || S.ActivePools().Count > 0 ? 0 : 1) : 2));
                return;
            }
            long diff = StartDiff();
            state = St.Starting;
            userPaused = tempPaused = pendingRestart = false;
            if (!silent) { runStart = DateTime.Now; restartTries = 0; }
            lastDiffCheck = DateTime.Now;
            last = null;
            int ticket = ++startTicket;
            Refresh();
            // the first start tunes cn/0 threads for this hardware (up to 30 s), so it runs off the UI thread
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Exception err = null;
                try { engine.Start(S, gpus, diff); } catch (Exception ex) { err = ex; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ticket != startTicket)
                    {
                        // stopped (or restarted) while the engine was starting: don't leave it running
                        if (state == St.Stopped) engine.Stop();
                        return;
                    }
                    if (err != null)
                    {
                        state = St.Stopped;
                        ShowBanner(L.F("b.start_failed", err.Message), L.T("nav.log"), () => Go(3));
                        Log("gui", L.F("log.start_error", err.Message));
                        Refresh();
                        return;
                    }
                    Log("gui", L.F("log.start", string.Join(", ", S.ActivePools()) + " · cn/0 · " +
                        (S.DiffMode == "off" ? L.T("log.diff_pool") : L.F("log.diff", diff)) +
                        " · CPU " + (S.CpuEnabled ? L.F("log.threads", engine.CpuThreadCount) : L.T("log.off")) +
                        " · CUDA " + (engine.CudaOn ? L.T("log.yes") : L.T("log.no")) + " · OpenCL " + (engine.OpenClOn ? engine.OpenClPlatform : L.T("log.no"))));
                    if (msg != null) ShowBanner(msg, L.T("b.change"), () => Go(0)); else UpdateBanner();
                    Refresh();
                }));
            });
        }

        void RestartEngine(string why)
        {
            Log("gui", L.F("log.restart", why));
            bool wasPaused = userPaused;
            engine.Stop();
            StartMining(true);
            if (wasPaused && state != St.Stopped) { userPaused = true; }
        }

        void PauseMining()
        {
            userPaused = true;
            ThreadPool.QueueUserWorkItem(_ => engine.Control("pause"));
            state = St.Paused;
            Log("gui", L.T("log.pause"));
            Refresh();
        }

        void ResumeMining()
        {
            userPaused = false;
            if (!tempPaused) ThreadPool.QueueUserWorkItem(_ => engine.Control("resume"));
            state = St.Running;
            Log("gui", tempPaused ? L.T("log.resume_cool") : L.T("log.resume"));
            Refresh();
        }

        void StopMining()
        {
            if (last != null && last.H60 > 0) S.LastHashrate = last.H60;
            startTicket++;
            engine.Stop();
            state = St.Stopped;
            userPaused = tempPaused = pendingRestart = false;
            S.Save();
            Log("gui", L.T("log.stopped"));
            UpdateBanner();
            Refresh();
            UpdateDeviceStats(null);
        }

        void OnEngineExited(int gen, int code)
        {
            if (gen != engine.Generation || state == St.Stopped) return;
            Log("gui", L.F("log.exited", code));
            state = St.Stopped;
            bool ranLong = (DateTime.Now - engine.StartedAt).TotalSeconds > 60;
            if (ranLong && restartTries < 5 && !exiting)
            {
                restartTries++;
                Log("gui", L.F("log.retry", restartTries));
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                t.Tick += (a, b) => { t.Stop(); if (state == St.Stopped && !exiting) StartMining(true); };
                t.Start();
            }
            else
            {
                ShowBanner(File.Exists(Engine.XmrigPath) ? L.F("b.engine_stopped", code) : L.T("b.engine_gone"), L.T("nav.log"), () => Go(3));
            }
            Refresh();
        }

        void Tick()
        {
            if (state != St.Stopped && !polling && engine.Running)
            {
                polling = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var snap = engine.Poll();
                    Dispatcher.BeginInvoke(new Action(() => { polling = false; OnSnapshot(snap); }));
                });
            }
            UpdateNavStatus();
        }

        void OnSnapshot(EngineSnapshot s)
        {
            if (state == St.Stopped || !s.Ok) return;
            last = s;
            if (state == St.Starting) state = userPaused ? St.Paused : St.Running;

            if ((DateTime.Now - lastSample).TotalSeconds >= 10)
            {
                lastSample = DateTime.Now;
                chart.Push(state == St.Paused ? 0 : s.H10);
            }
            if (s.H60 > 0) S.LastHashrate = s.H60;

            LearnDevices(s);
            TempGuard(s);
            AutoDiffCheck(s);
            Refresh();
            UpdateDeviceStats(s);
        }

        void LearnDevices(EngineSnapshot s)
        {
            bool changed = false;
            foreach (var d in s.Devices)
            {
                if (d.Type == "cpu") continue;
                string entry = d.Key + "|" + d.Name;
                int found = S.KnownDevices.FindIndex(x => x.StartsWith(d.Key + "|"));
                if (found < 0) { S.KnownDevices.Add(entry); changed = true; }
                else if (S.KnownDevices[found] != entry) { S.KnownDevices[found] = entry; changed = true; }
            }
            if (changed)
            {
                S.KnownDevices.Sort(StringComparer.Ordinal);
                S.Save();
                RebuildDevices();
            }
        }

        void TempGuard(EngineSnapshot s)
        {
            int max = -1;
            foreach (var d in s.Devices) if (d.Type != "cpu" && d.Temp > max) max = d.Temp;
            if (max < 0) return;
            if (!tempPaused && max >= S.GpuTempLimit)
            {
                tempPaused = true;
                ThreadPool.QueueUserWorkItem(_ => engine.Control("pause"));
                Log("gui", L.F("log.hot", max, S.GpuTempLimit));
            }
            else if (tempPaused && max <= S.GpuTempLimit - 8)
            {
                tempPaused = false;
                if (!userPaused) ThreadPool.QueueUserWorkItem(_ => engine.Control("resume"));
                Log("gui", L.F("log.cool", max));
            }
        }

        void AutoDiffCheck(EngineSnapshot s)
        {
            if (S.DiffMode != "auto" || state != St.Running || tempPaused || s.H60 <= 0) return;
            double minutes = (DateTime.Now - lastDiffCheck).TotalMinutes;
            bool first = engine.CurrentDiff <= 1000;
            if (minutes < (first ? 3 : 15)) return;
            lastDiffCheck = DateTime.Now;
            long want = AutoDiff(s.H60);
            long cur = engine.CurrentDiff;
            if (want > cur * 3 || want * 3 < cur)
                RestartEngine(L.F("log.autodiff", cur, want, Fmt.Hash(s.H60)));
        }

        // =============================================================================================
        // View updates
        // =============================================================================================

        void Refresh()
        {
            bool running = state == St.Running || state == St.Starting;
            if (state == St.Stopped) pill.Set(L.T("st.stopped"), "Muted", "Muted", "SegBg");
            else if (state == St.Starting) pill.Set(L.T("st.starting"), "Warn", "Warn", "WarnBg");
            else if (state == St.Paused || tempPaused) pill.Set(tempPaused && !userPaused ? L.T("st.overheat") : L.T("st.paused"), "Warn", "Warn", "WarnBg");
            else if (last != null && string.IsNullOrEmpty(last.Pool)) pill.Set(L.T("st.connecting"), "Warn", "Warn", "WarnBg");
            else pill.Set(L.T("st.running"), "Good", "GoodDot", "GoodBg");

            btnMain.Visibility = state == St.Stopped || state == St.Paused ? Visibility.Visible : Visibility.Collapsed;
            U.SetBtnText(btnMain, state == St.Paused ? L.T("m.resume") : L.T("m.start"), U.IcPlay);
            btnPause.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            btnStop.Visibility = state == St.Stopped ? Visibility.Collapsed : Visibility.Visible;

            var s = state != St.Stopped ? last : null;
            if (s != null)
            {
                double h = s.H10 > 0 ? s.H10 : s.H60;
                double u = Fmt.UnitOf(h);
                hashVal.Text = Fmt.Num(h, u);
                hashUnit.Text = " " + Fmt.UnitName(u);
                hashSub.Text = L.F("m.h60", s.H60 > 0 ? Fmt.Hash(s.H60) : "—", s.H15m > 0 ? Fmt.Hash(s.H15m) : "—");
                shareGood.Text = Fmt.Int(s.Good);
                shareBad.Text = " / " + Fmt.Int(s.Rejected);
                shareSub.Text = s.Total > 0 ? L.F("m.accepted_pct", Math.Round(s.Good * 100.0 / s.Total, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) : L.T("m.no_shares");
                diffVal.Text = s.Diff > 0 ? Fmt.Int(s.Diff) : "—";
                diffSub.Text = (string.IsNullOrEmpty(s.Pool) ? L.T("m.no_conn") : s.Pool) + (s.Ping > 0 ? " · " + L.F("m.ms", s.Ping) : "");
                int maxT = -1; string hot = null;
                foreach (var d in s.Devices) if (d.Type != "cpu" && d.Temp > maxT) { maxT = d.Temp; hot = d.Name; }
                tempVal.Text = maxT > 0 ? maxT.ToString() : "—";
                tempUnit.Text = maxT > 0 ? " °C" : "";
                tempSub.Text = maxT > 0 ? L.F("m.temp_sub", hot, S.GpuTempLimit) : L.T("m.no_gpu_data");
            }
            else
            {
                hashVal.Text = "—"; hashUnit.Text = ""; hashSub.Text = state == St.Stopped ? L.T("m.not_started") : L.T("m.waiting");
                shareGood.Text = "—"; shareBad.Text = ""; shareSub.Text = " ";
                diffVal.Text = "—"; diffSub.Text = S.DiffMode == "off" ? L.T("m.diff_pool") : (S.DiffMode == "fixed" ? L.F("m.diff_fixed", Fmt.Int(S.FixedDiff)) : L.T("m.diff_auto"));
                tempVal.Text = "—"; tempUnit.Text = ""; tempSub.Text = L.F("m.limit", S.GpuTempLimit);
            }
            chartUnit.Text = Fmt.UnitName(Fmt.UnitOf(chart.Max()));

            if (trayPause != null)
            {
                trayPause.Text = state == St.Stopped ? L.T("m.start") : (state == St.Paused ? L.T("m.resume") : L.T("m.pause"));
                string tip = "BCN Miner · " + (state == St.Stopped ? L.T("t.stopped") : state == St.Paused ? L.T("t.paused") : (s != null ? Fmt.Hash(s.H10 > 0 ? s.H10 : s.H60) : L.T("t.starting")));
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            UpdateNavStatus();
        }

        void UpdateNavStatus()
        {
            if (navState == null) return;
            if (state == St.Stopped)
            {
                navState.Text = L.T("st.stopped");
                navState.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                navDot.SetResourceReference(Shape.FillProperty, "Muted");
            }
            else
            {
                bool warn = state != St.Running || tempPaused;
                string label = state == St.Paused || tempPaused ? L.T("st.paused") : (state == St.Starting ? L.T("st.start_short") : L.T("st.running"));
                navState.Text = label + " · " + Fmt.Duration(DateTime.Now - runStart);
                navState.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Warn" : "Good");
                navDot.SetResourceReference(Shape.FillProperty, warn ? "Warn" : "GoodDot");
            }
            var parts = new List<string> { "CryptoNight" };
            if (last != null && state != St.Stopped)
            {
                int gpu = 0;
                foreach (var d in last.Devices)
                {
                    if (d.Type == "cpu") parts.Add(L.F("nav.cpu", d.Threads));
                    else gpu++;
                }
                if (gpu > 0) parts.Add(gpu + " GPU");
            }
            else parts.Add("cn/0");
            navAlgo.Text = string.Join(" · ", parts);
            navHeight.Text = height > 0 ? L.F("nav.block", Fmt.Int(height)) : L.T("nav.block_none");
        }

        void ShowBanner(string text, string action, Action onAction)
        {
            bannerText.Text = text;
            bannerBtn.Content = action;
            bannerBtn.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
            bannerAction = onAction;
            banner.Visibility = Visibility.Visible;
        }

        void UpdateBanner()
        {
            if (!Engine.Installed) { ShowBanner(L.T("b.no_engine"), L.T("nav.devices"), () => Go(2)); return; }
            if (pendingRestart && state != St.Stopped) { ShowBanner(L.T("b.changed"), L.T("b.restart"), () => RestartEngine(L.T("log.new_settings"))); return; }
            banner.Visibility = Visibility.Collapsed;
        }

        // =============================================================================================
        // Log
        // =============================================================================================

        void Log(string src, string msg)
        {
            AddLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "]  " + src.PadRight(8) + " " + msg);
        }

        void OnEngineLine(string line)
        {
            var m = RxHeight.Match(line);
            if (m.Success) { long h; if (long.TryParse(m.Groups[1].Value, out h)) height = h; }
            AddLine(line);
        }

        void AddLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            logLines.Add(line);
            if (logLines.Count > 4000) logLines.RemoveRange(0, 1000);
            if (logBox != null)
            {
                if (logBoxLines > 3000)
                {
                    logBox.Text = string.Join("\n", logLines.GetRange(Math.Max(0, logLines.Count - 2000), Math.Min(2000, logLines.Count))) + "\n";
                    logBoxLines = Math.Min(2000, logLines.Count);
                }
                else { logBox.AppendText(line + "\n"); logBoxLines++; }
                if (pages != null && pages[3].Visibility == Visibility.Visible) logBox.ScrollToEnd();
            }
            RefreshMiniLog();
        }

        void RefreshMiniLog()
        {
            if (miniLog == null) return;
            miniLog.Children.Clear();
            int from = Math.Max(0, logLines.Count - 6);
            if (logLines.Count == 0)
            {
                var t = U.T(L.T("m.log_empty"), 12, "Muted");
                t.FontFamily = U.Mono;
                miniLog.Children.Add(t);
                return;
            }
            for (int i = logLines.Count - 1; i >= from; i--)
            {
                var l = logLines[i];
                var m = RxStamp.Match(l);
                string shown = m.Success ? m.Groups[1].Value + " " + Regex.Replace(m.Groups[2].Value, @"\s{2,}", " ") : l;
                var t = new TextBlock { FontFamily = U.Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, i == logLines.Count - 1 ? 0 : 4, 0, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                U.Colorize(t, shown);
                miniLog.Children.Add(t);
            }
        }

        // =============================================================================================
        // Tray, autorun, closing
        // =============================================================================================

        void SetupTray()
        {
            tray = new System.Windows.Forms.NotifyIcon();
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetEntryAssembly().Location); } catch { }
            tray.Text = "BCN Miner";
            var menu = new System.Windows.Forms.ContextMenuStrip();
            trayShow = new System.Windows.Forms.ToolStripMenuItem("", null, (a, b) => ShowFromTray());
            trayPause = new System.Windows.Forms.ToolStripMenuItem("", null, (a, b) =>
            {
                if (state == St.Stopped || state == St.Paused) OnMain(); else PauseMining();
            });
            trayExit = new System.Windows.Forms.ToolStripMenuItem("", null, (a, b) => { exiting = true; Close(); });
            menu.Items.Add(trayShow);
            menu.Items.Add(trayPause);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(trayExit);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (a, b) => { if (b.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
            tray.Visible = true;
            UpdateTrayTexts();
        }

        void UpdateTrayTexts()
        {
            if (tray == null) return;
            trayShow.Text = L.T("t.show");
            trayExit.Text = L.T("t.exit");
            trayPause.Text = state == St.Stopped ? L.T("m.start") : (state == St.Paused ? L.T("m.resume") : L.T("m.pause"));
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void SetAutorun(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("BCN Miner", "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\" --minimized");
                    else k.DeleteValue("BCN Miner", false);
                }
            }
            catch (Exception ex) { Log("gui", L.F("log.autorun", ex.Message)); }
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!exiting && S.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(4000, "BCN Miner", state == St.Stopped ? L.T("t.hidden") : L.T("t.hidden_mining"), System.Windows.Forms.ToolTipIcon.Info);
                }
                return;
            }
            exiting = true;
            timer.Stop();
            if (last != null && last.H60 > 0) S.LastHashrate = last.H60;
            startTicket++;
            engine.Stop();
            S.Save();
            SystemEvents.UserPreferenceChanged -= OnSystemTheme;
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        void OnSystemTheme(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General || S.Theme != "system") return;
            Dispatcher.BeginInvoke(new Action(() => { Theme.Apply("system"); chart.InvalidateVisual(); }));
        }

        static void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { OpenFolder(Path.GetDirectoryName(path)); }
        }
    }
}

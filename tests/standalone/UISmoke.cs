using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BDownloader.Core;
using BDownloader.Views;
using Newtonsoft.Json.Linq;

internal static class UISmoke
{
    private static string artifacts;
    private static int checks, failures;
    private static bool online;
    private const string Example = "https://www.bilibili.com/list/161775300/?sid=1774529&spm_id_from=333.1387.0.0&oid=1956380285&bvid=BV1Ky411q7QC";

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string sandbox = AppDomain.CurrentDomain.BaseDirectory;
        if (sandbox.IndexOf(Path.Combine("analysis", "verification", "ui-sandbox") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
        { Console.Error.WriteLine("Refusing to run outside a dedicated ui-sandbox."); return 2; }
        Environment.CurrentDirectory = sandbox;
        artifacts = Path.Combine(sandbox, "artifacts"); Directory.CreateDirectory(artifacts);
        online = args.Contains("--online");
        var app = new BDownloader.App(); app.InitializeComponent();
        // WPF rejects a null StartupUri setter. Clear the backing field only in this
        // isolated host so Run(window) uses exactly one explicitly positioned window.
        FieldInfo startup = typeof(Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic);
        if (startup == null) throw new InvalidOperationException("WPF StartupUri backing field unavailable.");
        startup.SetValue(app, null);
        var window = new BDownloader.MainWindow { ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        var view = (DownloadView)window.FindName("Workspace");
        int stage = 0;
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        app.DispatcherUnhandledException += (sender, e) => {
            failures++; Console.WriteLine("FAIL UI dispatcher: " + e.Exception.GetType().Name + " " + e.Exception.Message);
            e.Handled = true; timer.Stop(); app.Shutdown(1);
        };
        timer.Tick += (sender, e) => {
            timer.Stop();
            try
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(130)) throw new TimeoutException("UI inspection timed out.");
                if (stage == 0)
                {
                    Require(window.IsLoaded && view.IsLoaded, "workspace has not loaded");
                    window.UpdateLayout();
                    Require(window.Title == "BDownloader" && window.Width == 1280 && window.Height == 860, "default window size/title");
                    Require(Control<DataGrid>(view, "EntriesGrid").Items.Count == 0, "sandbox queue is not empty");
                    Type[] windowTypes = typeof(BDownloader.App).Assembly.GetTypes().Where(x => !x.IsAbstract && typeof(Window).IsAssignableFrom(x)).ToArray();
                    Require(windowTypes.Length == 2 && windowTypes.Contains(typeof(BDownloader.MainWindow)) && windowTypes.Contains(typeof(AccountsWindow)), "unexpected application windows or routes");
                    Pass("standalone workspace has only its two intended windows and an empty isolated queue");
                    Layout(window, view); Save(window, "workspace-empty-1280x860.png");
                    Pass("default 1280x860 layout renders all essential controls without clipping");
                    window.Width = 1060; window.Height = 740; window.UpdateLayout(); Layout(window, view);
                    Save(window, "workspace-minimum-1060x740.png");
                    Pass("minimum 1060x740 layout retains link options grid and download controls");
                    window.Width = 1280; window.Height = 860; window.UpdateLayout();
                    Control<TextBox>(view, "OutputBox").Text = Path.Combine(sandbox, "no-downloads-started");
                    Control<ComboBox>(view, "ModeBox").SelectedIndex = 3;
                    Require(!Control<ComboBox>(view, "QualityBox").IsEnabled, "MP3 mode leaves quality enabled");
                    Control<ComboBox>(view, "ModeBox").SelectedIndex = 0;
                    Require(Control<ComboBox>(view, "QualityBox").IsEnabled, "video mode does not restore quality control");
                    Pass("download mode switches correctly enable or disable video quality");
                    stage = 1; timer.Start();
                    // ShowDialog runs a nested dispatcher; the next timer tick inspects and closes it.
                    Click(window, "◎    账号与登录"); return;
                }
                if (stage == 1)
                {
                    AccountsWindow accounts = app.Windows.OfType<AccountsWindow>().SingleOrDefault();
                    Require(accounts != null && accounts.IsVisible, "accounts button did not open the account window");
                    accounts.Left = -22000; accounts.Top = -22000; accounts.ShowInTaskbar = false; accounts.UpdateLayout();
                    Require(Control<Image>(accounts, "QrImage").Source == null, "account screen unexpectedly generated a login QR");
                    Require(Control<TextBlock>(accounts, "QrPlaceholder").Visibility == Visibility.Visible, "QR placeholder missing");
                    AccountsLayout(accounts);
                    double accountWidth = accounts.Width, accountHeight = accounts.Height;
                    accounts.Width = accounts.MinWidth; accounts.Height = accounts.MinHeight; accounts.UpdateLayout();
                    AccountsLayout(accounts);
                    accounts.Width = accountWidth; accounts.Height = accountHeight; accounts.UpdateLayout();
                    Save(accounts, "accounts-no-login.png");
                    Pass("actual account button opens the independent login screen without starting authentication");
                    stage = 2; timer.Start(); Click(accounts, "完成"); return;
                }
                if (stage == 2)
                {
                    Click(view, "B站播放列表 ↗");
                    Require(Control<TextBox>(view, "UrlBox").Text == Example, "sample button omitted playlist parameters");
                    Pass("actual sample button fills the complete provided Bilibili playlist URL");
                    if (online) Control<Button>(view, "ParseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else PopulateLayoutFixture(view);
                    stage = 3;
                }
                else if (stage == 3)
                {
                    if (!Control<Button>(view, "ParseButton").IsEnabled) { timer.Start(); return; }
                    DataGrid grid = Control<DataGrid>(view, "EntriesGrid");
                    string title = Control<TextBlock>(view, "CollectionTitle").Text;
                    Require(grid.Items.Count == 170, "expected 170 rows; found " + grid.Items.Count + "; " + Control<TextBlock>(view, "StatusText").Text);
                    Require(grid.Items.Cast<MediaRow>().All(x => x.Selected) && grid.Items.Cast<MediaRow>().Select(x => x.Entry.Id).Distinct().Count() == 170, "incomplete selection or duplicate rows");
                    Require(title.Contains("170 项"), "full collection count missing");
                    if (online) Require(Control<TextBlock>(view, "StatusText").Text.StartsWith("解析完成"), "live parse did not finish successfully");
                    File.WriteAllText(Path.Combine(artifacts, "collection-title.txt"), title, new UTF8Encoding(false));
                    Pass(online ? "actual Parse button resolves all 170 unique public videos with full title" : "clearly labeled synthetic 170-row fixture renders for offline layout inspection");
                    window.UpdateLayout(); Layout(window, view);
                    Save(window, online ? "workspace-live-170-top.png" : "workspace-SIMULATED-170-top.png");
                    Click(view, "清空选择"); Require(grid.Items.Cast<MediaRow>().All(x => !x.Selected), "clear selection omitted rows");
                    Click(view, "全选"); Require(grid.Items.Cast<MediaRow>().All(x => x.Selected), "select all omitted rows");
                    Pass("actual selection buttons toggle all 170 rows");
                    grid.ScrollIntoView(grid.Items[169]); window.UpdateLayout();
                    Save(window, online ? "workspace-live-170-bottom.png" : "workspace-SIMULATED-170-bottom.png");
                    Click(window, "↓    下载工作台");
                    Require(ReferenceEquals(view, window.FindName("Workspace")) && grid.Items.Count == 170, "workspace button replaced the queue");
                    Require(!Directory.Exists(Path.Combine(sandbox, "no-downloads-started")), "UI verification started a media download");
                    Pass("workspace navigation preserves all rows and starts no downloads");
                    app.Shutdown(); return;
                }
                timer.Start();
            }
            catch (Exception error)
            {
                failures++; Console.WriteLine("FAIL UI smoke: " + error.GetType().Name + " " + error.Message);
                try { Save(window, "ui-failure.png"); } catch { }
                app.Shutdown(1);
            }
        };
        app.Startup += (sender, e) => timer.Start();
        app.Run(window);
        if (failures == 0)
        {
            try {
                JObject state = JObject.Parse(File.ReadAllText(Path.Combine(sandbox, "Storage", "media-queue.json")));
                Require(((JArray)state["Rows"]).Count == 170, "exit did not persist all queue rows");
                Pass("application exit persists the complete sandbox queue");
            } catch (Exception ex) { failures++; Console.WriteLine("FAIL queue persistence: " + ex.Message); }
        }
        Console.WriteLine("RESULT checks=" + checks + " failures=" + failures + " online=" + online);
        return failures == 0 ? 0 : 1;
    }

    private static void PopulateLayoutFixture(DownloadView view)
    {
        var rows = (ObservableCollection<MediaRow>)typeof(DownloadView).GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
        for (int i = 1; i <= 170; i++) rows.Add(new MediaRow { Index = i, Selected = true, Status = "布局示例", Entry = new MediaEntry { Id = "sample-" + i, Title = "模拟布局条目 " + i.ToString("D3") + " · 示例视频标题", Url = "https://www.bilibili.com/video/BVsample" + i, DurationSeconds = 120 + i } });
        Control<TextBlock>(view, "CollectionTitle").Text = "界面布局样例（模拟数据） · 170 项";
        Control<TextBlock>(view, "StatusText").Text = "模拟数据，仅用于离线界面布局检查。";
    }
    private static void Layout(Window window, DownloadView view)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        var root = (FrameworkElement)window.Content;
        foreach (string name in new[] { "UrlBox", "ParseButton", "OutputBox", "ModeBox", "QualityBox", "EntriesGrid", "CancelButton", "DownloadButton" })
        {
            FrameworkElement item = Control<FrameworkElement>(view, name);
            Rect bounds = item.TransformToAncestor(root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            Require(item.IsVisible && bounds.Width > 20 && bounds.Height > 15 && bounds.Left >= -1 && bounds.Top >= -1
                && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1, name + " is clipped or outside the window");
        }
        Require(Control<DataGrid>(view, "EntriesGrid").ActualHeight >= 100, "download grid has no usable height");
        ScrollViewer scrolling = Descendants(Control<DataGrid>(view, "EntriesGrid")).OfType<ScrollViewer>().FirstOrDefault();
        Require(scrolling == null || scrolling.ScrollableWidth < 1, "download grid columns overflow horizontally");
    }
    private static void AccountsLayout(AccountsWindow accounts)
    {
        accounts.UpdateLayout(); accounts.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        foreach (Button button in Descendants(accounts).OfType<Button>())
        {
            DependencyObject parent = VisualTreeHelper.GetParent(button);
            while (parent != null && !(parent is Border && ((Border)parent).Padding.Top > 0)) parent = VisualTreeHelper.GetParent(parent);
            var card = parent as Border;
            if (card == null) continue;
            Rect bounds = button.TransformToAncestor(card).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Require(bounds.Left >= card.Padding.Left - 1 && bounds.Top >= card.Padding.Top - 1
                && bounds.Right <= card.ActualWidth - card.Padding.Right + 1 && bounds.Bottom <= card.ActualHeight - card.Padding.Bottom + 1,
                "account card clips button: " + button.Content);
        }
    }
    private static T Control<T>(FrameworkElement root, string name) where T : FrameworkElement
    { T item = root.FindName(name) as T; Require(item != null, "missing control " + name); return item; }
    private static void Click(DependencyObject root, string text)
    { Button button = Descendants(root).OfType<Button>().SingleOrDefault(x => Equals(x.Content, text)); Require(button != null && button.IsEnabled, "missing button " + text); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { DependencyObject child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in Descendants(child)) yield return item; } }
    private static void Save(Window window, string name)
    {
        window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        var content = (FrameworkElement)window.Content;
        // The Window visual paints the client area only; omit non-client resize/title
        // dimensions while retaining the content's external margin (account screen).
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right),
            (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(artifacts, name))) encoder.Save(file);
    }
    private static void Pass(string message) { checks++; Console.WriteLine("PASS " + message); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

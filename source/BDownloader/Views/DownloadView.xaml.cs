using BDownloader.Core;

using BDownloader.Services;

using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace BDownloader.Views
{
    public partial class DownloadView : UserControl
    {


        private readonly MediaEngine engine = new MediaEngine(AppPaths.RuntimeDirectory);
        private readonly ObservableCollection<MediaRow> rows = new ObservableCollection<MediaRow>();
        private CancellationTokenSource operation;
        private bool busy;
        private bool listComplete;
        private bool restored;
        private readonly string queueFile = Path.Combine(AppPaths.DataDirectory, "Storage", "media-queue.json");
        private const string BiliExample = "https://www.bilibili.com/list/161775300/?sid=1774529&spm_id_from=333.1387.0.0&oid=1956380285&bvid=BV1Ky411q7QC";
        private const string YouTubeExample = "https://www.youtube.com/watch?v=OfZiLFe2Efs";

        public DownloadView()
        {
            InitializeComponent();

            try { MediaAccounts.CleanupSessions(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            EntriesGrid.ItemsSource = rows;
            OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "BDownloader");
            Loaded += (s, e) => { Restore(); UpdateAccounts(); };
            // Cancel on application exit; navigating to login preserves the live queue.
            Application.Current.Exit += (s, e) => { operation?.Cancel(); Save(); };
        }

        private void BiliExample_Click(object sender, RoutedEventArgs e) { if (!busy) UrlBox.Text = BiliExample; }
        private void YouTubeExample_Click(object sender, RoutedEventArgs e) { if (!busy) UrlBox.Text = YouTubeExample; }
        private void UrlBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && !busy) Parse_Click(sender, e); }
        private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var row in rows) row.Selected = true; }
        private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var row in rows) row.Selected = false; }
        private void Mode_Changed(object sender, SelectionChangedEventArgs e) { if (QualityBox != null) QualityBox.IsEnabled = ModeBox.SelectedIndex < 2; }
        private void Cancel_Click(object sender, RoutedEventArgs e) { operation?.Cancel(); StatusText.Text = "正在停止… 已下载分片将用于继续下载。"; }

        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择视频与音频的保存位置", SelectedPath = OutputBox.Text })
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) OutputBox.Text = dialog.SelectedPath;
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { string path = Path.GetFullPath(OutputBox.Text); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { StatusText.Text = "无法打开目录：" + ex.Message; }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            ParseButton.IsEnabled = DownloadButton.IsEnabled = VerifyButton.IsEnabled = OptionsPanel.IsEnabled = OutputBox.IsEnabled = UrlBox.IsEnabled = !value;
            CancelButton.IsEnabled = value;
            EntriesGrid.IsReadOnly = value;
            if (value) { operation = new CancellationTokenSource(); OverallProgress.IsIndeterminate = true; }
            else { OverallProgress.IsIndeterminate = false; operation?.Dispose(); operation = null; }
        }

        private async void Parse_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            string url = UrlBox.Text.Trim();
            if (!SupportedUrl(url)) { StatusText.Text = "请输入完整的哔哩哔哩或 YouTube HTTPS 视频 / 播放列表链接。"; return; }
            SetBusy(true);
            StatusText.Text = "正在解析与核对播放列表，请稍候…";
            try
            {
                MediaCollection collection;
                using (var lease = MediaAccounts.CreateSession(url))
                {
                    if (BilibiliPlaylistResolver.IsListUrl(url))
                    {
                        var result = await new BilibiliPlaylistResolver().ResolveAsync(url, operation.Token);
                        if (!result.IsComplete) throw new InvalidDataException("播放列表分页不完整，未加入下载队列。请稍后重试。");
                        collection = new MediaCollection { Title = result.Title, SourceUrl = url, ExpectedEntryCount = result.ExpectedCount,
                            Entries = result.Entries.Select((x, i) => new MediaEntry { Id = x.Id, Title = x.Title, Url = x.Url, DurationSeconds = x.DurationSeconds, PlaylistIndex = i + 1 }).ToList() };
                    }
                    else collection = await engine.InspectAsync(url, lease.Session, operation.Token);
                }
                if (collection.Entries == null || collection.Entries.Count == 0) throw new InvalidDataException("没有找到可下载的视频。");
                if (collection.ExpectedEntryCount != collection.Entries.Count) throw new InvalidDataException("播放列表不完整：声明 " + collection.ExpectedEntryCount + " 项，取得 " + collection.Entries.Count + " 项。原下载队列已保留，请重试或检查账号权限。");
                // Replace the queue only after a complete inspection succeeds.
                rows.Clear();
                int index = 0;
                foreach (MediaEntry entry in collection.Entries) rows.Add(new MediaRow { Entry = entry, Index = ++index, Selected = true, Status = "等待下载" });
                listComplete = collection.ExpectedEntryCount == rows.Count;
                CollectionTitle.Text = (collection.Title ?? "下载列表") + " · " + rows.Count + " 项";
                StatusText.Text = listComplete ? "解析完成：共 " + rows.Count + " 项。选择下载内容后点击“下载选中”。" : "列表不完整，已阻止批量下载。请重新解析。";
                if (collection.Warnings != null && collection.Warnings.Count > 0) StatusText.Text += " " + string.Join("；", collection.Warnings);
                OverallProgress.Value = 0;
                Save();
            }
            catch (OperationCanceledException) { StatusText.Text = "解析已取消。"; }
            catch (Exception ex) { StatusText.Text = "解析失败：" + ex.Message; }
            finally { SetBusy(false); }
        }

        private MediaDownloadOptions Options(MediaSession session) => new MediaDownloadOptions {
            OutputDirectory = Path.GetFullPath(OutputBox.Text), Session = session,
            Mode = (MediaDownloadMode)ModeBox.SelectedIndex,
            MaximumHeight = ModeBox.SelectedIndex >= 2 ? 0 : new[] { 0, 2160, 1440, 1080, 720, 480 }[Math.Max(0, QualityBox.SelectedIndex)]
        };

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            EntriesGrid.CommitEdit();
            MediaDownloadOptions options;
            try { options = Options(null); Directory.CreateDirectory(options.OutputDirectory); }
            catch (Exception ex) { StatusText.Text = "下载目录无效：" + ex.Message; return; }
            var selected = rows.Where(x => x.Selected && !(x.Status == "完成 · 校验通过" && x.DownloadMode == options.Mode && x.MaximumHeight == options.MaximumHeight && File.Exists(x.FilePath) && string.Equals(Path.GetDirectoryName(x.FilePath), options.OutputDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))).ToList();
            if (!listComplete || selected.Count == 0) { StatusText.Text = !listComplete ? "请先解析完整的视频列表。" : "选中项目在当前格式、画质和目录下均已完成。可更改格式或重新校验。"; return; }
            SetBusy(true);
            int passed = 0, failed = 0;
            try
            {
                using (var lease = MediaAccounts.CreateSession(selected[0].Entry.Url))
                {
                    options.Session = lease.Session;
                    var progress = new Progress<MediaProgress>(p => {
                        var row = selected.FirstOrDefault(x => ReferenceEquals(x.Entry, p.Entry) || x.Entry.Url == p.Entry?.Url);
                        if (row != null) { row.Status = StateText(p.State); row.Progress = p.Percent; row.Detail = p.Message; }
                        OverallProgress.IsIndeterminate = p.State != MediaDownloadState.Downloading;
                        int batchIndex = row == null ? 0 : selected.IndexOf(row);
                        if (!OverallProgress.IsIndeterminate) OverallProgress.Value = (batchIndex + p.Percent / 100.0) * 100 / selected.Count;
                        string speed = p.BytesPerSecond > 0 ? " · " + (p.BytesPerSecond.Value / 1048576.0).ToString("0.00") + " MB/s" : "";
                        StatusText.Text = "[" + (batchIndex + 1) + "/" + selected.Count + "] " + (row == null ? "" : row.Title + " · ") + StateText(p.State) + speed;
                    });
                    // One item at a time allows durable history after each verified file.
                    foreach (var row in selected)
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        var results = await engine.DownloadAsync(new[] { row.Entry }, options, progress, operation.Token);
                        var result = results.Single();
                        row.FilePath = result.FilePath;
                        row.ReceiptPath = result.ReceiptPath;
                        row.DownloadMode = options.Mode;
                        row.MaximumHeight = options.MaximumHeight;
                        row.Status = result.Success ? "完成 · 校验通过" : result.Cancelled ? "已暂停" : "失败 · 可重试";
                        row.Detail = result.Success ? result.FilePath : result.Error;
                        row.Progress = result.Success ? 100 : row.Progress;
                        if (result.Success) passed++; else failed++;
                        Save();
                        if (result.Cancelled) break;
                    }
                }
                StatusText.Text = "本次校验通过 " + passed + " 项，失败 " + failed + " 项。" + (operation.IsCancellationRequested ? " 已暂停，可继续下载。" : failed > 0 ? " 鼠标停在失败行上查看原因，可选中重试。" : "");
                OverallProgress.Value = passed + failed == 0 ? 0 : passed * 100.0 / (passed + failed);
            }
            catch (OperationCanceledException) { StatusText.Text = "已暂停，可点击“下载选中 / 继续”续传。"; foreach (var row in selected.Where(x => x.Status != "完成 · 校验通过" && x.Status != "失败 · 可重试")) row.Status = "已暂停"; Save(); }
            catch (Exception ex) { StatusText.Text = "下载失败：" + ex.Message; Save(); }
            finally { SetBusy(false); }
        }

        private async void Verify_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            EntriesGrid.CommitEdit();
            var selected = rows.Where(x => x.Selected && !string.IsNullOrEmpty(x.FilePath)).ToList();
            if (selected.Count == 0) { StatusText.Text = "请选择已有下载文件的项目。"; return; }
            SetBusy(true);
            try
            {
                int passed = 0;
                foreach (var row in selected)
                {
                    row.Status = "正在重新校验";
                    var options = Options(null); options.Mode = row.DownloadMode; options.MaximumHeight = row.MaximumHeight;
                    StatusText.Text = "完整解码与校验：" + row.Title;
                    var result = await engine.VerifyAsync(row.FilePath, row.Entry, options, operation.Token);
                    if (result.Cancelled) { row.Status = "校验已取消"; Save(); throw new OperationCanceledException(operation.Token); }
                    row.Status = result.Success ? "完成 · 校验通过" : "校验失败";
                    row.Detail = result.Error ?? result.FilePath;
                    row.ReceiptPath = result.ReceiptPath;
                    if (result.Success) passed++;
                    Save();
                }
                StatusText.Text = "重新校验完成：" + passed + " / " + selected.Count + " 项通过。";
            }
            catch (OperationCanceledException) { StatusText.Text = "校验已取消。"; }
            catch (Exception ex) { StatusText.Text = "校验失败：" + ex.Message; }
            finally { SetBusy(false); }
        }

        public void ShowAccounts()
        {
            var window = new AccountsWindow { Owner = Window.GetWindow(this) };
            window.ShowDialog(); UpdateAccounts();
        }
        public void OpenOutputDirectory() => OpenFolder_Click(this, new RoutedEventArgs());
        private void Accounts_Click(object sender, RoutedEventArgs e) => ShowAccounts();
        private void UpdateAccounts()
        {
            AccountStatus.Text = "B站 · " + (BilibiliLogin.HasSession ? "已保存登录" : "未登录") + "     YouTube · " + (MediaAccounts.HasYouTube ? "已保存会话" : "匿名");
        }
        private static bool SupportedUrl(string value)
        {
            Uri uri; return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" && MediaEngine.IsSupportedUrl(value);
        }
        private static string StateText(MediaDownloadState state)
        {
            switch (state) { case MediaDownloadState.Resolving: return "正在解析"; case MediaDownloadState.Downloading: return "正在下载"; case MediaDownloadState.Processing: return "正在合并 / 转换"; case MediaDownloadState.Verifying: return "完整性校验中"; case MediaDownloadState.Completed: return "完成 · 校验通过"; case MediaDownloadState.Failed: return "失败 · 可重试"; case MediaDownloadState.Cancelled: return "已暂停"; default: return "等待下载"; }
        }

        private sealed class QueueState { public string Url { get; set; } public string Folder { get; set; } public string Title { get; set; } public int Mode { get; set; } public int Quality { get; set; } public bool Complete { get; set; } public List<MediaRow> Rows { get; set; } }
        private void Save()
        {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(queueFile));
                string temporary = queueFile + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(new QueueState { Url = UrlBox.Text, Folder = OutputBox.Text, Title = CollectionTitle.Text, Mode = ModeBox.SelectedIndex, Quality = QualityBox.SelectedIndex, Complete = listComplete, Rows = rows.ToList() }, Formatting.Indented));
                if (File.Exists(queueFile)) File.Replace(temporary, queueFile, null); else File.Move(temporary, queueFile);
            } catch (Exception ex) { StatusText.Text += " 队列保存失败：" + ex.Message; }
        }
        private void Restore()
        {
            if (restored) return; restored = true;
            try {
                if (!File.Exists(queueFile)) return;
                var state = JsonConvert.DeserializeObject<QueueState>(File.ReadAllText(queueFile));
                if (state == null) return;
                UrlBox.Text = state.Url; OutputBox.Text = state.Folder; CollectionTitle.Text = state.Title;
                ModeBox.SelectedIndex = Math.Max(0, Math.Min(3, state.Mode)); QualityBox.SelectedIndex = Math.Max(0, Math.Min(5, state.Quality)); listComplete = state.Complete;
                foreach (var row in state.Rows ?? new List<MediaRow>()) { if (row.Status != "完成 · 校验通过") row.Status = "等待继续"; else if (!File.Exists(row.FilePath)) row.Status = "文件已丢失"; rows.Add(row); }
                StatusText.Text = "已恢复 " + rows.Count + " 项下载记录。";
            } catch (Exception ex) { StatusText.Text = "无法恢复下载记录：" + ex.Message; }
        }
    }

    public sealed class MediaRow : INotifyPropertyChanged
    {
        public MediaEntry Entry { get; set; }
        public int Index { get; set; }
        [JsonIgnore] public string Title => Entry?.Title;
        [JsonIgnore] public string Duration => Entry?.DurationSeconds > 0 ? TimeSpan.FromSeconds(Entry.DurationSeconds.Value).ToString(@"hh\:mm\:ss") : "待解析";
        private bool selected; public bool Selected { get => selected; set { selected = value; Changed("Selected"); } }
        private string status; public string Status { get => status; set { status = value; Changed("Status"); } }
        private double progress; public double Progress { get => progress; set { progress = value; Changed("ProgressText"); } }
        [JsonIgnore] public string ProgressText => Progress.ToString("0.0") + "%";
        private string detail; public string Detail { get => detail; set { detail = value; Changed("Detail"); } }
        public string FilePath { get; set; }
        public string ReceiptPath { get; set; }
        public MediaDownloadMode DownloadMode { get; set; }
        public int MaximumHeight { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

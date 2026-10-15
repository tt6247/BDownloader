using BDownloader.Services;
using Microsoft.Win32;
using QRCoder;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BDownloader.Views
{
 public partial class AccountsWindow : Window
 {
  private CancellationTokenSource loginCancellation;
  public AccountsWindow()
  {
   InitializeComponent(); Refresh();
   Closed += (s,e) => { loginCancellation?.Cancel(); loginCancellation?.Dispose(); };
  }
  private void Refresh()
  {
   BiliStatus.Text = BilibiliLogin.HasSession ? "已保存登录" : "未登录";
   YouTubeStatus.Text = MediaAccounts.HasYouTube ? "已保存会话，下载时检查有效性。" : "尚未导入会话";
  }
  private async void Qr_Click(object sender,RoutedEventArgs e)
  {
   loginCancellation?.Cancel(); loginCancellation?.Dispose();
   var source = loginCancellation = new CancellationTokenSource();
   CancellationToken token = source.Token;
   QrButton.IsEnabled=false; QrStatus.Text="正在生成二维码…";
   try
   {
    var request = await BilibiliLogin.GenerateAsync(token);
    token.ThrowIfCancellationRequested();
    QrImage.Source=RenderQr(request.Url); QrPlaceholder.Visibility=Visibility.Collapsed;
    QrStatus.Text="请使用哔哩哔哩客户端扫码"; QrButton.IsEnabled=true;
    while (!token.IsCancellationRequested)
    {
     await Task.Delay(1200,token);
     var result = await BilibiliLogin.PollAsync(request.Key,token);
     token.ThrowIfCancellationRequested();
     if (result.Code==0)
     {
      if (!BilibiliLogin.SaveCookies(result.Cookies)) throw new InvalidOperationException("登录信息不完整，请刷新二维码重试。");
      QrImage.Source=null; QrPlaceholder.Text="✓  登录成功"; QrPlaceholder.Visibility=Visibility.Visible;
      QrStatus.Text="登录已安全保存，可以开始下载。"; Refresh(); return;
     }
     if (result.Code==86038) { QrStatus.Text="二维码已过期，请点击刷新。"; return; }
     QrStatus.Text=result.Code==86090 ? "扫码成功，请在手机上确认。" : result.Code==86101 ? "等待扫码…" : "等待手机确认…";
    }
   }
   catch (OperationCanceledException) { }
   catch (Exception error) { if (!token.IsCancellationRequested) QrStatus.Text="登录失败："+error.Message; }
   finally { if (ReferenceEquals(loginCancellation,source)) QrButton.IsEnabled=true; }
  }
  public static BitmapImage RenderQr(string url)
  {
   using (var generator=new QRCodeGenerator())
   using (var data=generator.CreateQrCode(url,QRCodeGenerator.ECCLevel.L))
   using (var code=new QRCode(data))
   using (var bitmap=code.GetGraphic(6))
   using (var stream=new MemoryStream())
   {
    bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png); stream.Position=0;
    var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad;
    image.StreamSource=stream; image.EndInit(); image.Freeze(); return image;
   }
  }
  private void BiliLogout_Click(object sender,RoutedEventArgs e)
  {
   try { loginCancellation?.Cancel(); BilibiliLogin.Logout(); QrImage.Source=null; QrPlaceholder.Text="点击下方生成二维码"; QrPlaceholder.Visibility=Visibility.Visible; QrStatus.Text="已退出登录。"; Refresh(); }
   catch(Exception error) { QrStatus.Text=error.Message; }
  }
  private void YouTubeLogin_Click(object sender,RoutedEventArgs e)
  { try { Process.Start(new ProcessStartInfo("https://www.youtube.com/account"){UseShellExecute=true}); } catch { YouTubeStatus.Text="请在浏览器打开 YouTube 并登录。"; } }
  private void Import_Click(object sender,RoutedEventArgs e)
  {
   var dialog=new OpenFileDialog{Filter="Cookie 文本|*.txt|所有文件|*.*"};
   if(dialog.ShowDialog(this)!=true)return;
   try { MediaAccounts.ImportYouTube(dialog.FileName); Refresh(); } catch(Exception error){YouTubeStatus.Text=error.Message;}
  }
  private void YouTubeLogout_Click(object sender,RoutedEventArgs e)
  { try{MediaAccounts.ClearYouTube();Refresh();}catch(Exception error){YouTubeStatus.Text=error.Message;} }
  private void Close_Click(object sender,RoutedEventArgs e)=>Close();
 }
}

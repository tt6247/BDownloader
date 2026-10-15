using System;
using System.IO;
using System.Windows;
using BDownloader.Services;
namespace BDownloader
{
 public partial class MainWindow : Window
 {
  public MainWindow() { InitializeComponent(); if (!File.Exists(Path.Combine(AppPaths.RuntimeDirectory,"yt-dlp.exe"))) EngineStatus.Text="●  下载组件缺失"; }
  private void Workspace_Click(object sender,RoutedEventArgs e) { Workspace.Focus(); }
  private void Accounts_Click(object sender,RoutedEventArgs e) { Workspace.ShowAccounts(); }
  private void Folder_Click(object sender,RoutedEventArgs e) { Workspace.OpenOutputDirectory(); }
 }
}

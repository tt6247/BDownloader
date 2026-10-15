# BDownloader

独立的 Windows 视频与音频下载器。推荐从 [Releases 下载单文件版 BDownloader.exe](https://github.com/tt6247/BDownloader/releases/latest)，约 **42.8 MiB**，只需携带这个 EXE。

支持哔哩哔哩与 YouTube、视频与纯音频、完整播放列表批量下载、登录会话、断点续传和下载完整性校验。源码及构建脚本位于本仓库，单文件程序和预编译依赖单独放在 Releases。

## 单文件与精简

单文件版把界面、托管依赖、yt-dlp、FFmpeg 和 QuickJS-NG 一并压缩内置，不需要手动解压或另外安装下载工具。首次启动自动展开并逐文件校验到 `%LOCALAPPDATA%/BDownloader/Runtime/<版本摘要>/payload/`，以后复用已验证缓存，发现损坏会重新展开。

账号与队列独立保存到 `%LOCALAPPDATA%/BDownloader/Data/`，移动 EXE、更新版本或修复运行时缓存不会清除这些数据。目录版原有 `app/Config/` 和 `app/Storage/` 保留在原位；两种版本的数据目录不同，单文件版首次使用需重新登录。

本版以约 2.1 MB 的 QuickJS-NG 替换约 97.5 MB 的 Deno，并去掉发行版调试符号。替换运行时后已验证 YouTube 的实际 JavaScript 解题、两平台完整下载及媒体校验。单文件仍依赖 Windows 提供的 .NET Framework 4.7.2 或更高版本。

## 新界面

深色侧边栏与浅色下载工作台，统一管理链接、保存设置、批量任务和账号。应用的项目名、命名空间、程序集、窗口标题、启动入口均使用 **BDownloader**；旧界面、旧导航和旧程序集依赖已从当前源码及发行目录移除。

## 下载

1. 粘贴 B站或 YouTube 的视频 / 播放列表 HTTPS 链接，点击「解析内容」。示例按钮可以填入测试链接。
2. 确认解析出的全部条目，自由勾选。B站新版系列 / 合集读取全部分页，核对总数、重复和缺页。
3. 选择视频含音频、仅视频、M4A 或 MP3，以及画质上限与保存位置。
4. 点击「下载选中 / 继续」。支持批量顺序下载、暂停和断点续传，失败项目可重试。
5. 下载完成后自动检查轨道、时长、分辨率、全文件解码和 SHA-256，通过才显示完成。旁边保存 `*.verification.json`，可随时「重新校验」。

更改格式、画质或目录可下载另一份输出。队列与结果原子保存在数据目录的 `Storage/media-queue.json`，重新启动可恢复；失败原因可在行提示中查看。

## 账号

- **B站**：打开「账号与登录」，生成二维码，用手机客户端扫码并确认。可在同一窗口退出。扫码服务为独立实现，支持二维码刷新、过期状态及关闭后取消轮询。
- **YouTube**：从账号窗口打开浏览器完成 Google 登录，导入本机导出的 Netscape 格式 `cookies.txt`。仅保留 YouTube 域有效 Cookie；可随时退出本地登录。
- 两个平台的会话都按当前 Windows 用户使用 DPAPI 加密，保存于数据目录的 `Config/`。公开内容可匿名下载；保存会话后，服务器有效性及权限在解析 / 下载时检查。

## 验证

用户提供的 B站列表本次完整识别 **170 / 170** 项、ID 无重复。B站代表视频及 YouTube 示例已实际完整下载；视频含音频、仅视频、M4A 和 MP3 均有媒体实测。新独立版另外执行分页、下载引擎、会话和界面回归。

本机验收通过：48 项离线回归、10 项界面检查、12 项登录检查、6 项真实联网下载检查，以及 7 项单文件集成检查。运行下方脚本可在隔离目录重新生成 `analysis/verification/` 验证结果。测试未替用户扫码确认，也未复制用户浏览器登录。

## 运行与重建

Windows x64，.NET Framework 4.7.2 或更高版本。附带 yt-dlp、QuickJS-NG 与 FFmpeg，无需安装 Python 或 Visual Studio。

```powershell
# 首次在其他机器准备编译工具，本机已准备好
.\scripts\prepare-build-tools.ps1

# 从 Releases 下载 BDownloader-build-dependencies.zip，校验并准备原生/托管依赖
.\scripts\prepare-app-dependencies.ps1 -ArchivePath .\BDownloader-build-dependencies.zip

# 编译独立程序与准备下载运行时
.\scripts\build.ps1

# 制作单一运行文件，输出 releases/BDownloader.exe
.\scripts\build-single-file.ps1

# 验证单文件首次展开、缓存复用、修复、并发启动及真实界面启动
.\scripts\verify-single-file.ps1

# 离线回归
.\scripts\verify.ps1

# 界面渲染与按钮验证；-Online 加入真实列表解析
.\scripts\verify-ui.ps1 -Online

# 独立二维码与加密会话回归；-Online 加入未扫码轮询
.\scripts\verify-login.ps1 -Online

# 联网完整下载与校验公开示例
.\scripts\verify-online.ps1

# 生成不含账号与下载记录的便携包
.\scripts\package.ps1
```

媒体运行时固定为 yt-dlp 2026.08.19、QuickJS-NG 0.17.0，按官方发布摘要与固定 SHA-256 校验。QuickJS 属于 [yt-dlp 官方支持的运行时](https://github.com/yt-dlp/yt-dlp/wiki/EJS#quickjs--quickjs-ng)。第三方许可证内置于单文件载荷；源码采用 [GPL-3.0](LICENSE)，依赖详情见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 项目结构

| 路径 | 内容 |
|---|---|
| `source/BDownloader/` | 当前独立 WPF 项目与源码 |
| `source/BDownloader/Core/` | 双平台下载、完整性检查和播放列表解析 |
| `source/BDownloader/Services/` | B站二维码、两平台加密会话 |
| `source/BDownloader/Views/` | 新下载工作台与独立账号窗口 |
| `source/BundleLauncher/` | 单文件缓存展开、校验和启动 |
| `app/` | 可运行程序、下载工具与依赖 |
| `releases/BDownloader.exe` | 可直接双击的单一发行文件 |
| `tests/standalone/`、`scripts/` | 当前构建与验证入口 |
| `source/BDownloader/lib/` | 已提交许可证；二进制依赖由 Release 依赖包准备 |

Git 不包含账号、Cookie、队列、下载视频、运行日志、本机归档、编译缓存或预编译二进制。

会员画质、私密视频及地区内容需要对应账号权限。完整性校验针对取得的媒体，时长容差为 2 秒与 3% 中较大者。新独立版使用自己的加密会话，需要在新账号窗口登录。

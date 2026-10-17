# 构建与本地开发

## 环境

在 Windows x64 上开发和构建，安装 .NET Framework 4.7.2 或更高版本，并准备 PowerShell 与 Git。构建脚本使用系统的 Framework MSBuild，以及下载到项目内的 Roslyn 4.8.0 和 net472 引用程序集 1.0.3；不要求安装 Visual Studio 或 Python。

首次准备需要访问 GitHub、NuGet。私有仓库与发行附件需要具有仓库访问权限的 GitHub 账号。以下命令均在仓库根目录执行。

## 从源码开始

```powershell
git clone https://github.com/tt6247/BDownloader.git
Set-Location BDownloader

# 准备编译器和 .NET Framework 引用程序集
.\scripts\prepare-build-tools.ps1
```

从 [Releases](https://github.com/tt6247/BDownloader/releases) 下载 `BDownloader-build-dependencies.zip`，放到仓库根目录后执行：

```powershell
# 校验压缩包摘要、六个条目及各自摘要，再恢复本地依赖
.\scripts\prepare-app-dependencies.ps1 -ArchivePath .\BDownloader-build-dependencies.zip

# 编译 WPF 程序，同时准备 yt-dlp、QuickJS-NG 和许可证
.\scripts\build.ps1

# 运行目录版
.\app\BDownloader.exe
```

`source/BDownloader/lib/` 的二进制文件由依赖包恢复，Git 只跟踪许可证。恢复脚本固定依赖包及文件摘要，不能把任意同名压缩包当成有效依赖。

## 构建入口

| 脚本 | 作用与输出 |
|---|---|
| `prepare-build-tools.ps1` | 准备 `.tools/` 中的编译器和引用程序集 |
| `prepare-app-dependencies.ps1` | 恢复 FFmpeg、Newtonsoft.Json、QRCoder 及许可证 |
| `prepare-media-tools.ps1` | 准备固定版本 yt-dlp，并调用 QuickJS 准备脚本 |
| `prepare-quickjs.ps1` | 核对上游发布信息与固定摘要，安装 `qjs.exe` |
| `build.ps1` | 默认生成 Release 目录版到 `app/`，日志在 `build/standalone/build.log` |
| `build-single-file.ps1` | 默认重建目录版，再生成 `releases/BDownloader.exe` 及发行校验文件 |
| `package.ps1` | 可选目录与源码便携包；不等同于主发行单文件 |

```powershell
# 调试构建，输出到独立目录
.\scripts\build.ps1 -Configuration Debug -OutputDirectory .\build\debug-app

# 默认单文件构建
.\scripts\build-single-file.ps1

# 已确认 app/ 就是待发布构建时，可跳过重复编译
.\scripts\build-single-file.ps1 -SkipAppBuild
```

WPF 项目为 `source/BDownloader/BDownloader.csproj`，目标框架 net472，目标平台 x64。Release 开启优化且不生成 PDB，Debug 保留调试符号。目录版程序与单文件启动器都叫 `BDownloader.exe`，但内容和摘要不同，验证时应区分。

## 常见问题

| 现象 | 处理 |
|---|---|
| 缺少编译器或引用程序集 | 执行 `prepare-build-tools.ps1` |
| 缺少 FFmpeg 或托管 DLL | 下载正式依赖包并执行 `prepare-app-dependencies.ps1` |
| 依赖摘要不符 | 重新获取对应版本附件；不要跳过摘要检查 |
| 程序正在运行，无法覆盖输出 | 关闭对应目录版程序，或指定另一 `OutputDirectory` |
| 上游下载失败 | 检查 GitHub / NuGet 网络与代理；本地已有有效缓存可复用 |
| 程序启动但账号为空 | 单文件与目录版的数据根目录不同，参见[数据文档](DATA.md) |

生成的 `app/`、`build/`、`releases/`、`.tools/` 不进入 Git。完整验收步骤见[测试文档](TESTING.md)。

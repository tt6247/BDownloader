# 发布与维护

源码仓库：[tt6247/BDownloader](https://github.com/tt6247/BDownloader)。仓库可见性与访问权限以 GitHub 当前设置为准。应用版本发布在 [Releases](https://github.com/tt6247/BDownloader/releases)。

## 发布顺序

1. 确认待发布源码范围、更新 [CHANGELOG](../CHANGELOG.md) 和受影响的开发文档。
2. 按[构建文档](BUILD.md)准备依赖并构建目录版。
3. 按[测试文档](TESTING.md)执行相关回归，保留应用摘要及结果。
4. 执行 `build-single-file.ps1 -SkipAppBuild` 打包已验证的 `app/`，再执行 `verify-single-file.ps1`。如果需要重新编译应用，应重新核对验收结果与摘要。
5. 检查 `git status`、差异及忽略规则，只提交源码、测试、脚本、许可证和整理过的文档。
6. 推送源码，创建指向该发布提交的版本标签；在 GitHub 创建同名发行草稿。
7. 上传下列资产，核对远端文件大小与 SHA-256 后再发布为正式版本。

仅整理文档时提交并同步主分支即可，不必重建 EXE、移动已有版本标签或新增程序发行版本。版本标签应与对应的发行源码和二进制一致。

## 发行资产

| 文件 | 用途 |
|---|---|
| `BDownloader.exe` | 正常使用需要的单文件程序 |
| `BDownloader.exe.sha256` | 外层 EXE 摘要 |
| `BDownloader.exe.manifest.json` | 发行审计清单，包含内置文件大小、摘要及载荷摘要 |
| `BDownloader-build-dependencies.zip` | 源码重建所需的预编译依赖 |
| `BDownloader-build-dependencies.zip.sha256` | 依赖包摘要 |

前三项由单文件构建脚本生成。依赖包和配套摘要是另外维护的发行附件，单文件构建脚本不会生成它们。普通用户只需 EXE；外部清单不是程序启动必需品，也不是视频旁的校验 JSON。

发布说明应写明改动、系统要求、执行过的验证、主程序摘要和资产用途。不要把旧版本测试描述为新版本已重新执行，也不要上传账号、Cookie、用户队列、下载视频、缓存或未经整理的本机日志。

## 当前发行基准

| 版本 | 发布源码提交 | 单文件 SHA-256 |
|---|---|---|
| [v1.0.1](https://github.com/tt6247/BDownloader/releases/tag/v1.0.1) | `81b2ff661aa77e99b7b289260d3905da5738f0cb` | `88d83f16e8a399f3ad4713ce9b087bf140b7b54811bc507acc631a58f514060c` |
| [v1.0.0](https://github.com/tt6247/BDownloader/releases/tag/v1.0.0) | `4a94a9e5179fdd71cf82ac755a21152eb155ff68` | `2fdd2ffd799cb7b0dd376347971528b212ad18e5c4a0aed8eea7e7032c98ea2a` |

v1.0.1 外层 EXE 为 44,873,728 字节，内层应用摘要为 `de1da7d4794476702ddc14f0c95ad5e070c142e26ba66f4768fc512fb0b3b59a`。主分支后续文档提交不改变这些既有发行基准。

## 依赖与缓存维护

当前固定版本以脚本和[第三方说明](../THIRD_PARTY_NOTICES.md)为准：yt-dlp 2026.08.19、QuickJS-NG 0.17.0、Roslyn 4.8.0、net472 引用程序集 1.0.3。FFmpeg 与托管库由单独的依赖包提供。

升级媒体工具时更新准备脚本中的版本、来源、文件大小和固定摘要，以及许可证 / 上游证明文件；真实验证两平台解析、完整下载、JavaScript 解题及全文件解码。修改依赖包时同步更新 `prepare-app-dependencies.ps1` 中的整包摘要与条目摘要，不能只替换同名附件。

单文件载荷按固定文件顺序和时间戳打包，启动器逐项检查内嵌清单。它是会展开缓存的单一分发文件，不是零落地运行。发现缓存损坏时启动器隔离并重新展开；缓存中的 `.invalid-*` 与旧载荷目录和持久数据是不同内容。人工清理前关闭相关程序，并只处理确认的缓存目录，不能把整个数据根目录当成缓存删除。

项目源码许可证为 GPL-3.0，第三方组件保留各自许可证。变更依赖或重新分发时同步维护许可证说明及内置通知。

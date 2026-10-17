# 测试与验收

先按[构建文档](BUILD.md)生成 `app/`。脚本使用隔离目录加载应用和依赖，不复制用户账号或队列。测试产物位于被 Git 忽略的 `analysis/`，不能把含本机路径或会话信息的原始报告直接当成公开文档。

## 入口与覆盖

| 命令 | 覆盖与当前预期 |
|---|---|
| `.\scripts\verify.ps1` | 51 项离线回归：列表 18、引擎 17、媒体账号 10、路径 6 |
| `.\scripts\verify-ui.ps1` | 10 项 WPF 布局、按钮和交互检查；默认使用测试列表 |
| `.\scripts\verify-ui.ps1 -Online` | 同一套界面检查，加入真实 B站列表解析 |
| `.\scripts\verify-login.ps1` | 11 项离线二维码及会话逻辑检查 |
| `.\scripts\verify-login.ps1 -Online` | 12 项，包含真实二维码生成及未扫码轮询；不自动扫码授权 |
| `.\scripts\verify-online.ps1 -InspectOnly` | 3 项公开链接解析检查，不下载完整媒体 |
| `.\scripts\verify-online.ps1` | 6 项，含真实解析与公开媒体完整下载、解码、摘要比对 |
| `.\scripts\verify-single-file.ps1` | 7 项，先构建 `releases/BDownloader.exe` 再执行 |

各脚本可用 `-ApplicationDirectory` 指定目录版输出；单文件脚本改用 `-BundlePath` 指定 EXE。在线测试受网络、平台风控、视频是否仍可用影响，不能用一次历史通过替代新版本实际验收。

离线引擎测试会生成短媒体，以真实 FFmpeg 检查轨道、时长、分辨率和截断损坏，并用模拟下载器检查进度、隔离损坏文件、重试及取消子进程。v1.0.1 还覆盖旧 JSON 成功迁移、失败 / 取消保留基准、不同目录同名文件独立记录，以及不删除无关 JSON。

单文件测试覆盖首次展开、复用、损坏修复、并发启动、两类目录联接拒绝，以及真实 GUI 启动与数据目录稳定性。部分 Windows 沙箱会限制命名互斥锁或目录联接操作，此类权限失败需在具备相应权限的隔离环境复验，不能算作检查通过。

## 结果位置

| 文件或目录 | 含义 |
|---|---|
| `analysis/verification/standalone-summary.json` | 最近一次离线汇总，含各套件日志路径与应用摘要 |
| `analysis/verification/ui-summary.json` | 最近一次界面检查结果 |
| `analysis/verification/standalone-login-summary.json` | 最近一次登录检查结果 |
| `analysis/verification/standalone-live-summary.json` | 最近一次联网检查结果 |
| `analysis/verification/single-file-summary.json` | 最近一次单文件检查与发行 EXE 摘要 |
| `analysis/verification/standalone/<运行号>/` | 离线测试日志与隔离宿主 |
| `analysis/verification/bundle-sandbox/<运行号>/` | 单文件缓存、报告与启动沙箱 |

## 版本验收记录

| 范围 | 结果 | 说明 |
|---|---|---|
| v1.0.1，2026-10-16 | 离线 51/51、单文件 7/7 | 对应校验记录迁移与清理的实际发行构建 |
| v1.0.0，2026-10-15 | 离线 48/48、界面 10/10、登录在线 12/12、联网 6/6、单文件 7/7 | 历史版本验收，不能算作 v1.0.1 新跑的在线检查 |
| 2026-10-17 文档整理 | 文档链接、命令及源码引用核对 | 无应用功能改动，不重建发行程序 |

历史公开样例为用户提供的 B站列表（`sid=1774529`，当时完整解析 170 项）及 YouTube 视频 `OfZiLFe2Efs`。测试地址在 `tests/standalone/LiveDownloadTests.cs` 中；列表条目数可能随平台内容变化，以当次完整分页结果为准。

## 按改动选择验证

修改下载、列表或校验时至少执行离线回归；涉及平台解析或媒体运行时升级时，再做在线解析与完整下载。修改界面时执行界面脚本，修改登录时执行登录脚本。发布单文件前构建并执行单文件检查。

核对报告时区分目录版应用摘要与外层单文件摘要。构建后再修改源码或覆盖 `app/`，原报告就不能自动证明新构建通过；只有报告中的摘要与待发布文件对应，才可记录为该发行版本的验收结果。

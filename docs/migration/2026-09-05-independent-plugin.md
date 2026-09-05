# 2026-09-05 独立插件迁移记录

## 结果与边界

BiliDownloader 已从 avalonia_dock_simple_test 的内置 Plugins 迁入本仓库。原命名空间、BiliDownloader.dll、Module、Plugin/Document/Tool ID、两个创建意图、3.0.0 插件版本、schema 3 文档内容和本地存储路径/纪元均保持不变。

原目录的 294 个受版本控制文件全部建立迁移映射，其中 **194 个生产 C#/XAML 文件逐字节保持一致**。没有重写下载、登录、内容源、调度、FFmpeg、字幕弹幕、限速、持久化及决策代码。完整源文件摘要、目标摘要和必要改动原因见 [source-inventory.json](source-inventory.json)。

## 迁移映射

| 原位置（相对宿主仓库） | 新位置（相对本仓库） |
| --- | --- |
| Plugins/BiliDownloader/BiliDownloader | src/BiliDownloaderPlugin.Plugin，文档另行归入 docs |
| Plugins/BiliDownloader/BiliDownloader.Tests | tests/BiliDownloaderPlugin.Tests |
| Plugins/BiliDownloader/BiliDownloader.ReleaseAcceptance | tools/BiliDownloader.ReleaseAcceptance |
| 插件 doc/design、doc/reference、doc/plan-history | docs 下同名分类，完整保留设计和阶段决策 |
| Host.UiTests 两个 Bili 界面测试 | tests/BiliDownloaderPlugin.UiTests，改用 Standalone 主题及临时目录上下文 |
| Host.UiTests 的 Bili 保存竞争测试 | tests/BiliDownloaderPlugin.HostUiTests |
| Host.PluginTests 的 Bili 包加载与真实注册组合测试 | tests/BiliDownloaderPlugin.HostTests |
| docs/plan-history 下 Host V2/V3 G12 Bili 专属记录 | docs/plan-history 下同路径 |
| TestResults/BiliDownloader/G8 | TestResults/BiliDownloader/G8，保留历史证据 |

宿主综合历史保留原事实，仅修正迁出后的文档链接并增加说明。历史已经失效的代码链接明确标记为历史引用；不重建早已删除的架构，也不把旧发布记录改写为新验收。

## 必要适配

- Plugin 的项目引用改为 SDK/UI 3.3.0 NuGet 包，采用 Build 1.1.2；保留原业务依赖版本和私有资产所有权。项目目录名沿用新骨架，AssemblyName/RootNamespace 继续为 BiliDownloader。
- 原测试程序集名保留为 BiliDownloader.Tests；原发布验收工具及历史排除项保留。覆盖率脚本更新项目路径，并为 C 组目录匹配增加斜杠边界，避免新目录名 .Plugin 被误算为 Plugin 代码组；阈值完全不变。
- 新增单文档、单调度工具的 Standalone 注册适配、数据路径和取消/释放流程。窗口复用原 Model/View，加载 Fluent/Semi/Ursa 和原界面需要的 App 资源键。
- Standalone 数据位于 %LOCALAPPDATA%/BiliDownloaderPlugin/Standalone/BiliDownloader；ResetDirectories 只含该目录。正式插件仍使用原 BiliDataPaths，不搬运或重置用户数据。
- HostTests/HostUiTests 不进入默认解决方案；显式 HostRepositoryRoot 联调复用现有友元程序集身份，不新增宿主内部 API，也不把宿主源码纳入插件。
- 删除宿主内置源码、解决方案条目、专属测试和 Gate 的默认 Bili 构建/部署项；通用容器和生命周期测试改用独立夹具，保留原断言意义。

## 验证结果

| 检查 | 结果 |
| --- | --- |
| 独立解决方案 Release，warnings as errors | 0 警告、0 错误 |
| 原业务测试 + 5 项 Standalone 适配测试 | 734/734，通过、0 跳过 |
| 独立 Headless UI，包括真实对象图窗口启动、双栏、关闭及失败状态 | 12/12，通过、0 跳过 |
| 真实宿主 ZIP Loader/Module/Registry/Workspace | 2/2，通过 |
| 真实宿主文档保存捕获与修改竞争 | 1/1，通过 |
| 主项目移除 Bili 后 Release 构建 | 0 警告、0 错误 |
| 主项目 PluginTests / UiTests / Host Unit / Gate Tests | 218 / 67 / 292 / 19 项，通过、0 跳过 |
| 生产 C#/XAML 原样迁移校验 | 194/194 SHA-256 一致 |
| 覆盖率 | 行 83.87%、分支 67.86%；A 89.19%/77.25%、B 85.22%/69.58%、C 76.91%/57.02%，原门禁通过 |

正式包为 BiliDownloader-3.0.0-win-x64.zip，14 个文件，包含 SQLite win-x64 原生库；不含 Standalone、Tests、SDK、Avalonia、Dock 或 Microsoft.Extensions 共享 DLL。配对外部清单和解压文件均通过 SHA-256 核对。

离线窗口截图位于本地 TestResults/Migration/ui，已检查浅色和深色主题。普通 UI 回归沿用原 HeadlessDrawing 测量模式；像素截图单独启用 Skia，仅运行 StandaloneWindowTests。两种字体测量方式不混用既有布局阈值。

此轮没有运行真实账号扫码、真实 Bilibili 下载或固定媒体发布验收；既有离线业务协议和本地 HTTP 测试保持完整。ReleaseAcceptance 工具已迁移并构建，历史 P0/P1 中未完成的实网、媒体发布事项继续保留。

## 产物清理与原工作区保护

主项目 Plugins/BiliDownloader 和现有 Release Controls/BiliDownloader 均已移出。永久删除旧二进制缓存曾被自动审批阻止；采用可逆隔离，将已核对的旧构建产物移入本仓库 TestResults/Migration/old-build-20260905。该目录不参与构建、部署或版本控制，现有宿主输出不再发现旧插件。

主项目中其他插件的未提交修改保留。README 和文档索引在已有内容上更新迁出链接；本次改动文件统一为仓库原有 LF 换行，未覆盖其他业务修改。没有提交 Git、推送或修改正式用户数据。

## 重复验证

在仓库根目录执行 README 中的独立构建、测试及覆盖率命令。真实宿主联调使用 scripts/Test-HostIntegration.ps1；脚本重建包、验证配对摘要，在新的隔离目录解压并运行两个联调项目，不部署到正在使用的宿主。

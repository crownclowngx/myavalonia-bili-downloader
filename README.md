# BiliDownloader 独立插件

> V6.1 图标同步升级：插件 `3.0.1`，Core/UI SDK `3.4.0`，Build `1.1.3`。
> 图标映射、兼容边界与验证命令见 [专用说明](docs/plan-history/v6.1-plugin-icons.md)。

由 MyAvaloniaManagement 内置 BiliDownloader 完整迁出的 Managed Plugin。业务、界面、命名空间和程序集名保持原样，正式插件 ID 为 `myavalonia.plugin.bili-downloader`，版本为 `3.0.1`。

## 开始开发

在本仓库根目录运行：

```powershell
dotnet restore BiliDownloaderPlugin.slnx
dotnet build BiliDownloaderPlugin.slnx -c Release -warnaserror
dotnet run --project src/BiliDownloaderPlugin.Standalone
```

Standalone 左侧是原下载文档，右侧是原调度工具，可以拖动中间分隔条。它执行真实登录、解析和下载业务；调试数据位于 `%LOCALAPPDATA%\BiliDownloaderPlugin\Standalone\BiliDownloader`，与正式插件隔离。下载文件目录仍由原设置界面控制。

## 项目分工

| 项目 | 职责 |
| --- | --- |
| src/BiliDownloaderPlugin.Plugin | 原 BiliDownloader 业务、Module、Document、Tool、View 和资源；输出 BiliDownloader.dll |
| src/BiliDownloaderPlugin.Standalone | 单文档与单调度工具调试窗口、注册适配、独立数据路径和生命周期 |
| tests/BiliDownloaderPlugin.Tests | 原业务测试及新增 Standalone 生命周期测试 |
| tests/BiliDownloaderPlugin.UiTests | 无宿主依赖的 Headless 界面测试 |
| tools/BiliDownloader.ReleaseAcceptance | 保留的离线媒体、限速、包与历史发布验收工具 |
| tests/BiliDownloaderPlugin.HostTests / HostUiTests | 显式指定宿主源码后运行的独立联调测试，不进入默认解决方案 |

## 测试与发布

```powershell
dotnet test BiliDownloaderPlugin.slnx -c Release
.\tests\BiliDownloaderPlugin.Tests\Run-Tests.ps1 -NoRestore -KeepResults
dotnet msbuild src/BiliDownloaderPlugin.Plugin/BiliDownloaderPlugin.Plugin.csproj -t:BuildManagedPluginPackage -p:Configuration=Release
.\scripts\Test-HostIntegration.ps1 -HostRepositoryRoot D:\code\bishe\common\avalonia_dock_simple_test
```

正式 ZIP 和配对清单位于 `src/BiliDownloaderPlugin.Plugin/artifacts/managed-plugin-packages/`，ZIP 为 `BiliDownloader-3.0.1-win-x64.zip`，内部目录保持 `Controls/BiliDownloader`。独立构建使用 SDK/UI 3.4.0 和 Build 1.1.3 NuGet 包，不需要宿主源码。

要部署到已退出的宿主，显式指定其 Controls 目录：

```powershell
dotnet msbuild src/BiliDownloaderPlugin.Plugin/BiliDownloaderPlugin.Plugin.csproj -t:DeployManagedPlugin -p:Configuration=Release -p:ManagedPluginDeployRoot=C:\Path\To\Host\Controls
```

只安装一份同 ID 插件；普通 build/run 不会自动部署到主项目。Standalone、Tests、宿主共享程序集不会进入正式插件 ZIP。

## 文档

- [完整文档索引](docs/README.md)
- [迁移记录与验证结果](docs/migration/2026-09-05-independent-plugin.md)
- [项目和窗口职责](docs/project-and-window-responsibilities.md)
- [部署与发布说明](docs/deployment-and-release.md)
- [产品与业务能力](docs/reference/PRODUCT.md)
- [原测试门禁与历史证据](tests/BiliDownloaderPlugin.Tests/TESTING.md)

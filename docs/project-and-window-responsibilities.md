# 项目、Host 与 Standalone 窗口职责

Plugin 项目保留原 BiliDownloader.Module 注册的唯一业务对象图，输出 BiliDownloader.dll。稳定 Plugin、Document、Tool ID 和创建意图保持不变。Model、View、服务、私有资源和生命周期都属于 Plugin；只依赖公开 SDK/UI，不引用宿主内部实现。

Standalone 是一个下载文档加一个调度工具的开发工作台。PreviewPluginRegistration 调用真实 Module，记录 Document/Tool/View/Lifecycle 类型并建立 scoped Document、singleton Tool 和 Lifecycle，不复制服务清单。窗口通过这些注册结果解析真实 Model 与 View。

PreviewRuntime 拥有一个插件 Provider 和一个 Document Scope，提供 IDocumentLifetime。启动异步执行原 Lifecycle 再初始化 Document；失败时停止后台组件、释放 Scope 和 Provider。关闭先发出 Document 取消信号，再等待原 Lifecycle 停止下载、登录验证及进度落盘，最后释放对象图。窗口不阻塞 UI 线程等待初始化。

StandaloneDataPaths 只覆盖调试数据路径；正式插件继续使用原 BiliDataPaths。纪元清理只覆盖调试根下的 BiliDownloader 叶子目录，不读取或重置正式账号、任务、日志和密钥。调试状态跨重启保留，下载输出目录继续由原业务设置控制。

Standalone 加载 Fluent、Semi、Ursa 主题，并补齐原界面引用的 App 资源键。它使用原有弹窗、登录和文件选择交互；不模拟完整 Dock、工作区多文档、文件保存协调器或安装升级流程。

默认解决方案只包含 Plugin、Standalone、业务/UI 测试和 ReleaseAcceptance。HostTests 与 HostUiTests 只用于显式联调，通过 HostRepositoryRoot 引用真实宿主，并沿用已授权的友元测试程序集身份。这些项目和开发程序均不进入插件 ZIP，不需要扩大 SDK 或宿主公开 API。

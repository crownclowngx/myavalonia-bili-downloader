using MyAvaloniaManagement.Business.Diagnostics;
using MyAvaloniaManagement.Business.Lifecycle;
using MyAvaloniaManagement.Business.Workspace;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;


namespace MyAvaloniaManagement.PluginTests;

/// <summary>
/// 使用四个当前 Managed Plugin 的真实构建输出验证统一入口、deps 解析和公共契约共享。
/// </summary>
public sealed class BiliPackageCompositionTests
{
    [Fact]
    public void G12最终测试Zip通过真实V3发现组合并进入Workspace目录()
    {
        var packageRoot = Environment.GetEnvironmentVariable(
            "MYAVALONIA_G12_V3_PACKAGE_ROOT");
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            // 普通回归不重复打包；G12 专项脚本设置目录后必须执行本测试。
            return;
        }

        var snapshot = AssemblyLoaderHelper.Discover(Path.GetFullPath(packageRoot));
        Assert.Empty(snapshot.Diagnostics);
        var assembly = Assert.Single(snapshot.Assemblies);
        Assert.Equal("BiliDownloader", assembly.GetName().Name);
        var catalog = PluginModuleCatalog.Discover(snapshot);
        var diagnosticsRoot = Path.Combine(
            Path.GetTempPath(), $"bili-g12-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(diagnosticsRoot);
        using var diagnostics = HostDiagnosticSession.Start(diagnosticsRoot);
        var registryBuilder = new PluginRegistryBuilder();
        using var pluginProviders = new PluginProviderOwner();
        var documentScopes = new DocumentScopeRegistry();
        var services = new ServiceCollection();
        services.AddApplicationServices(registryBuilder, pluginProviders, documentScopes);
        services.AddViewModels();
        services.AddSingleton(diagnostics);
        services.AddSingleton<IHostDiagnosticSink>(diagnostics);
        services.AddSingleton(catalog);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        try
        {
            pluginProviders.Compose(
                catalog, provider, registryBuilder, documentScopes, diagnostics);
            var registry = provider.GetRequiredService<PluginRegistry>();
            var plugin = Assert.Single(registry.Plugins);
            Assert.Equal("myavalonia.plugin.bili-downloader", plugin.Manifest.PluginId.Value);
            Assert.Single(plugin.DocumentTypes);
            Assert.Single(plugin.ToolTypes);
            var lifecycle = Assert.Single(registry.Lifecycles);
            Assert.Equal(plugin.Manifest.PluginId.Value, lifecycle.OwnerId.Value);
            Assert.Equal(
                [plugin.Manifest.PluginId.Value],
                pluginProviders.AvailablePluginIds.Select(pluginId => pluginId.Value));

            // 最终包不能只证明 Registry 中存在描述符。这里不启动会读写用户 SQLite/设置的
            // Lifecycle；其执行行为由 G12 插件测试在隔离数据路径上覆盖。确认 Lifecycle 已进入
            // 真实 Registry 后，只推进 Host 自己的可用性投影，再验证 Workspace 消费同一冻结目录。
            provider.GetRequiredService<PluginLifecycleStateStore>().SetState(
                new PluginLifecycleState(
                    new PluginId("myavalonia.plugin.bili-downloader"),
                    PluginLifecycleStatus.Ready));
            var workspace = provider.GetRequiredService<WorkspaceSession>();
            var creationMenu = provider.GetRequiredService<DocumentCreationMenuQuery>();
            var documentEntries = creationMenu.ReadDirectory().Items.Select(item => item.Entry).Where(entry =>
                entry.DocumentTypeId.Value.StartsWith(
                    "myavalonia.plugin.bili-downloader.document.",
                    StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, documentEntries.Length);
            Assert.Equal(
                ["personal-source", "quick-url"],
                documentEntries.Select(entry => entry.CreationIntentId!.Value).Order().ToArray());
            Assert.True(workspace.GetAvailableToolDescriptors().ContainsKey(
                new ToolTypeId("myavalonia.plugin.bili-downloader.tool.scheduler")));
        }
        finally
        {
            documentScopes.CloseAll();
            diagnostics.Dispose();
            Directory.Delete(diagnosticsRoot, recursive: true);
        }
    }

}

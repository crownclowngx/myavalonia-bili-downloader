[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HostRepositoryRoot,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$NoPackage
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$hostSource = (Resolve-Path -LiteralPath $HostRepositoryRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $hostSource 'Host/MyAvaloniaManagement/MyAvaloniaManagement.csproj'))) {
    throw 'HostRepositoryRoot 必须指向宿主源码根目录。'
}
Push-Location $repository
$previousPackageRoot = $env:BILIDOWNLOADER_PACKAGE_ROOT
$previousControlsRoot = $env:MYAVALONIA_G12_V3_PACKAGE_ROOT
try {
    if (-not $NoPackage) {
        dotnet msbuild src/BiliDownloaderPlugin.Plugin/BiliDownloaderPlugin.Plugin.csproj -t:BuildManagedPluginPackage "-p:Configuration=$Configuration" -v:minimal
        if ($LASTEXITCODE -ne 0) { throw '插件打包失败。' }
    }
    $package = Join-Path $repository 'src/BiliDownloaderPlugin.Plugin/artifacts/managed-plugin-packages/BiliDownloader-3.0.0-win-x64.zip'
    $packageManifest = Get-Content -Raw -LiteralPath ($package -replace '\.zip$', '.manifest.json') | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $packageManifest.archive.sha256) {
        throw 'ZIP 摘要与配对清单不一致。'
    }
    $runRoot = Join-Path $repository ('TestResults/HostIntegration/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    Expand-Archive -LiteralPath $package -DestinationPath $runRoot
    foreach ($asset in $packageManifest.files) {
        $assetPath = [IO.Path]::GetFullPath((Join-Path $runRoot $asset.path))
        if (-not $assetPath.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw '资产路径越出本次解压目录。'
        }
        if ((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash -ne $asset.sha256) {
            throw "资产摘要不一致：$($asset.path)"
        }
    }
    $env:MYAVALONIA_G12_V3_PACKAGE_ROOT = Join-Path $runRoot 'Controls'
    $env:BILIDOWNLOADER_PACKAGE_ROOT = Join-Path $runRoot 'Controls/BiliDownloader'
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $env:BILIDOWNLOADER_PACKAGE_ROOT 'plugin.manifest.json') | ConvertFrom-Json
    if ($manifest.pluginId -ne 'myavalonia.plugin.bili-downloader') { throw '插件身份漂移。' }
    foreach ($pattern in @('MyAvaloniaManagement*','Avalonia*','Dock*','Microsoft.Extensions.*','*Standalone*','*Tests*')) {
        if (Get-ChildItem -LiteralPath $env:BILIDOWNLOADER_PACKAGE_ROOT -Recurse -File | Where-Object Name -Like $pattern) {
            throw "正式包混入共享或开发资产：$pattern"
        }
    }
    foreach ($project in @('HostTests','HostUiTests')) {
        dotnet test "tests/BiliDownloaderPlugin.$project/BiliDownloaderPlugin.$project.csproj" -c $Configuration "-p:HostRepositoryRoot=$hostSource" --logger trx --results-directory $runRoot -v minimal
        if ($LASTEXITCODE -ne 0) { throw "$project 验收失败。" }
    }
    Write-Output "真实宿主联调通过，证据目录：$runRoot"
}
finally {
    $env:BILIDOWNLOADER_PACKAGE_ROOT = $previousPackageRoot
    $env:MYAVALONIA_G12_V3_PACKAGE_ROOT = $previousControlsRoot
    Pop-Location
}

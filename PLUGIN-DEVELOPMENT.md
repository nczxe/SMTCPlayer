# SMTC Player 插件开发指南

本指南介绍如何为 SMTC Player 开发插件，扩展媒体控制功能。

---

## 目录

1. [插件系统概述](#插件系统概述)
2. [开发环境准备](#开发环境准备)
3. [插件项目结构](#插件项目结构)
4. [核心接口](#核心接口)
5. [plugin.json 清单](#pluginjson-清单)
6. [实现示例](#实现示例)
7. [事件系统](#事件系统)
8. [设置存储](#设置存储)
9. [日志系统](#日志系统)
10. [构建与部署](#构建与部署)
11. [最佳实践](#最佳实践)
12. [常见问题](#常见问题)

---

## 插件系统概述

SMTC Player 插件系统基于 .NET AssemblyLoadContext 实现，具有以下特性：

- **可收集加载**：插件使用 `isCollectible: true` 的 AssemblyLoadContext，支持热更新和动态卸载
- **契约隔离**：通过 `SMTCPlayer.PluginApi` 零依赖程序集定义接口，宿主与插件共享同一份类型
- **事件广播**：使用有界 Channel（256 容量）进行异步事件分发，单读者顺序处理
- **能力声明**：插件通过 `capabilities` 声明提供的功能，宿主聚合后上报网页端

### 架构流程

```
SMTC 变化 → MainViewModel 轮询 → diff 生成 PluginEvent → 有界 Channel → 后台顺序分发 → 所有插件
```

---

## 开发环境准备

### 必需工具

- .NET 10 SDK（版本 10.0.302 或更高）
- Visual Studio 2022 或 JetBrains Rider
- Git（可选，用于版本控制）

### 项目依赖

插件项目只需引用 `SMTCPlayer.PluginApi` 契约程序集，无需依赖宿主或其他库。

---

## 插件项目结构

### 标准目录布局

```
MyPlugin/
├── MyPlugin.csproj              # 项目文件
├── MyPlugin.cs                  # 插件主类（实现 IPlugin）
├── MySearchProvider.cs          # 搜索提供者（可选，实现 ISearchProvider）
├── plugin.json                  # 插件清单
└── Properties/
    └── launchSettings.json      # 调试配置（可选）
```

### 项目文件配置

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Library</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\smtc-ui\SMTCPlayer.PluginApi\SMTCPlayer.PluginApi.csproj" />
  </ItemGroup>
</Project>
```

---

## 核心接口

### IPlugin（必需）

所有插件必须实现此接口：

```csharp
namespace SMTCPlayer.PluginApi;

public interface IPlugin
{
    // 插件加载完成后调用一次，传入受控上下文
    Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken);
    
    // 应用退出前调用，插件应在此保存状态
    Task OnUnloadingAsync(CancellationToken cancellationToken);
    
    // 收到 Core 广播的事件
    Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken);
}
```

### ISearchProvider（可选）

实现此接口可承接网页端的搜索与播放请求：

```csharp
namespace SMTCPlayer.PluginApi;

public interface ISearchProvider
{
    // 按关键词搜索，返回统一结构的结果列表
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken);
    
    // 在系统已打开的音乐软件中播放指定结果
    Task<bool> PlayAsync(SearchResultItem item, CancellationToken cancellationToken);
}

public sealed record SearchResultItem
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Artists { get; init; } = "";
    public string Album { get; init; } = "";
    public double Duration { get; init; }  // 毫秒
    public string? Cover { get; init; }
}
```

### IPluginContext（上下文）

插件加载时通过 `OnLoadedAsync` 接收的上下文对象：

```csharp
namespace SMTCPlayer.PluginApi;

public interface IPluginContext
{
    // 最近一次轮询得到的媒体快照（只读）
    MediaSnapshot Current { get; }
    
    // 本地后端服务基础地址（如 http://127.0.0.1:8888）
    string ServerUrl { get; }
    
    // 受控播放控制（异步转发到后端 API）
    IPlaybackController Playback { get; }
    
    // 插件专属日志（自动带插件标识前缀）
    IPluginLogger Log { get; }
    
    // 插件专属的隔离键值设置存储
    IPluginSettings Settings { get; }
}
```

### IPlaybackController（播放控制）

```csharp
namespace SMTCPlayer.PluginApi;

public interface IPlaybackController
{
    Task<bool> PlayAsync();
    Task<bool> PauseAsync();
    Task<bool> TogglePlayPauseAsync();
    Task<bool> NextAsync();
    Task<bool> PreviousAsync();
    Task<bool> SetVolumeAsync(int volume);  // 0-100
    Task<bool> ToggleMuteAsync();
}
```

---

## plugin.json 清单

每个插件目录必须包含 `plugin.json` 文件：

```json
{
  "id": "my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "author": "作者名",
  "description": "插件功能描述",
  "assembly": "MyPlugin.dll",
  "entryType": "MyNamespace.MyPlugin",
  "apiVersion": 1,
  "minHostVersion": "1.1.1",
  "capabilities": ["search", "playlists"]
}
```

### 字段说明

| 字段 | 必需 | 说明 |
|------|------|------|
| `id` | 是 | 插件唯一标识符，用于路由和数据目录命名 |
| `name` | 是 | 显示名称 |
| `version` | 是 | 语义化版本号 |
| `author` | 是 | 作者名称 |
| `description` | 是 | 功能描述 |
| `assembly` | 是 | 编译后的 DLL 文件名 |
| `entryType` | 是 | 插件主类的完全限定名（命名空间.类名） |
| `apiVersion` | 否 | 所需的插件 API ABI 版本（当前为 1） |
| `minHostVersion` | 否 | 所需的最低宿主版本 |
| `capabilities` | 否 | 提供的能力列表：`search`（搜索）、`playlists`（歌单） |

---

## 实现示例

### 基础插件模板

```csharp
using SMTCPlayer.PluginApi;

namespace MyPlugin;

public sealed class MyPlugin : IPlugin
{
    private IPluginContext _ctx = null!;

    public Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        _ctx.Log.Info("插件已加载");
        return Task.CompletedTask;
    }

    public Task OnUnloadingAsync(CancellationToken cancellationToken)
    {
        // 保存设置
        _ctx.Settings.Save();
        _ctx.Log.Info("插件已卸载");
        return Task.CompletedTask;
    }

    public Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
    {
        switch (pluginEvent.Type)
        {
            case PluginEventType.SongChanged:
                _ctx.Log.Info($"切歌: {pluginEvent.After?.Artist} - {pluginEvent.After?.Title}");
                break;
                
            case PluginEventType.PlaybackStateChanged:
                _ctx.Log.Info($"播放状态: {(pluginEvent.After?.IsPlaying == true ? "播放中" : "已暂停")}");
                break;
                
            case PluginEventType.VolumeChanged:
                _ctx.Log.Debug($"音量: {pluginEvent.After?.Volume}");
                break;
                
            case PluginEventType.ServerStateChanged:
                _ctx.Log.Info($"服务状态: {(pluginEvent.IsServerRunning ? "运行中" : "已停止")}");
                break;
        }
        return Task.CompletedTask;
    }
}
```

### 带搜索功能的插件

```csharp
using SMTCPlayer.PluginApi;

namespace MyMusicPlugin;

public sealed class MyMusicPlugin : IPlugin, ISearchProvider
{
    private IPluginContext _ctx = null!;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        _ctx.Log.Info("音乐插件已加载");
        return Task.CompletedTask;
    }

    public Task OnUnloadingAsync(CancellationToken cancellationToken)
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    public Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
    {
        // 处理事件...
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        // 实现搜索逻辑
        var results = new List<SearchResultItem>();
        
        // 示例：调用外部 API
        using var resp = await _http.GetAsync($"https://api.example.com/search?q={Uri.EscapeDataString(query)}", cancellationToken);
        resp.EnsureSuccessStatusCode();
        
        // 解析结果并添加到列表
        // ...
        
        return results;
    }

    public async Task<bool> PlayAsync(SearchResultItem item, CancellationToken cancellationToken)
    {
        // 实现播放逻辑
        // 例如：启动外部播放器或调用 API
        _ctx.Log.Info($"播放: {item.Artists} - {item.Name}");
        return true;
    }
}
```

---

## 事件系统

### 事件类型

```csharp
public enum PluginEventType
{
    SongChanged,           // 切歌：标题/歌手/专辑/歌曲 ID/来源变化
    PlaybackStateChanged,  // 播放状态变化：播放 ↔ 暂停/停止
    VolumeChanged,         // 音量或静音状态变化
    ServerStateChanged     // 后端服务启动/停止/连接断开
}
```

### 事件数据

```csharp
public sealed class PluginEvent
{
    public PluginEventType Type { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public MediaSnapshot? Before { get; init; }  // 变化前快照
    public MediaSnapshot? After { get; init; }    // 变化后快照
    public bool IsServerRunning { get; init; }    // 仅 ServerStateChanged 有效
}

public sealed record MediaSnapshot
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AlbumTitle { get; init; } = "";
    public string Status { get; init; } = "stopped";
    public string? Source { get; init; }
    public bool IsPlaying { get; init; }
    public bool HasPrevious { get; init; }
    public bool HasNext { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
    public double Volume { get; init; }
    public bool Muted { get; init; }
    public long? SongId { get; init; }
}
```

### 事件处理最佳实践

```csharp
public Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
{
    switch (pluginEvent.Type)
    {
        case PluginEventType.SongChanged:
            // 检查快照是否有效
            if (pluginEvent.After?.Title is string title && !string.IsNullOrEmpty(title))
            {
                // 处理切歌事件
            }
            break;
            
        case PluginEventType.ServerStateChanged:
            if (pluginEvent.IsServerRunning)
            {
                // 服务启动：初始化资源
            }
            else
            {
                // 服务停止：清理资源
            }
            break;
    }
    return Task.CompletedTask;
}
```

---

## 设置存储

### 使用 IPluginSettings

```csharp
// 读取设置（带默认值）
var interval = _ctx.Settings.Get("check_interval_seconds", 60);
var enabled = _ctx.Settings.Get("feature_enabled", true);
var username = _ctx.Settings.Get<string>("username", null);

// 写入设置（仅内存）
_ctx.Settings.Set("last_check", DateTime.Now);
_ctx.Settings.Set("feature_enabled", false);

// 持久化到磁盘
_ctx.Settings.Save();

// 重新加载（丢弃未保存的修改）
_ctx.Settings.Reload();
```

### 设置存储位置

每个插件的设置独立存储在：
```
%LocalAppData%/SMTCPlayer/plugins-data/<plugin-id>/settings.json
```

---

## 日志系统

### 使用 IPluginLogger

```csharp
_ctx.Log.Debug("调试信息");
_ctx.Log.Info("普通信息");
_ctx.Log.Warn("警告信息");
_ctx.Log.Error("错误信息");
_ctx.Log.Error("错误信息", exception);
```

### 日志输出示例

```
[2026-08-23 14:30:25] [INFO] [my-plugin] 插件已加载
[2026-08-23 14:30:26] [DEBUG] [my-plugin] 检查间隔: 60s
[2026-08-23 14:30:30] [WARN] [my-plugin] 网络连接超时
```

---

## 构建与部署

### 构建插件

```bash
cd plugins/MyPlugin
dotnet build -c Release
```

### 部署目录结构

编译后，插件需要部署到以下位置之一：

1. **应用程序目录旁的 plugins 文件夹**（推荐）：
   ```
   SMTCPlayer.exe
   plugins/
   ├── my-plugin/
   │   ├── plugin.json
   │   ├── MyPlugin.dll
   │   └── 其他依赖文件...
   ```

2. **用户本地数据目录**：
   ```
   %LocalAppData%/SMTCPlayer/plugins/
   ├── my-plugin/
   │   ├── plugin.json
   │   └── MyPlugin.dll
   ```

### 自动部署（MSBuild）

在插件项目文件中添加：

```xml
<Target Name="DeployPlugin" AfterTargets="Build">
  <ItemGroup>
    <PluginFiles Include="$(OutputPath)**\*" />
  </ItemGroup>
  <Copy SourceFiles="@(PluginFiles)" 
        DestinationFolder="$(SolutionDir)\..\smtc-ui\plugins\my-plugin\%(RecursiveDir)" />
</Target>
```

### 手动部署

```bash
# 创建插件目录
mkdir -p smtc-ui/plugins/my-plugin

# 复制文件
cp plugins/MyPlugin/bin/Release/net10.0/* smtc-ui/plugins/my-plugin/
cp plugins/MyPlugin/plugin.json smtc-ui/plugins/my-plugin/
```

---

## 最佳实践

### 1. 资源管理

```csharp
public sealed class MyPlugin : IPlugin, IDisposable
{
    private HttpClient? _http;
    private Timer? _timer;
    
    public Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _http = new HttpClient();
        _timer = new Timer(CheckStatus, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }
    
    public Task OnUnloadingAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        _timer?.Dispose();
        _http?.Dispose();
    }
}
```

### 2. 错误处理

```csharp
public Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
{
    try
    {
        // 处理事件
    }
    catch (Exception ex)
    {
        _ctx.Log.Error($"处理事件失败: {pluginEvent.Type}", ex);
    }
    return Task.CompletedTask;
}
```

### 3. 异步操作

```csharp
public async Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken)
{
    _ctx = context;
    
    // 异步初始化
    await InitializeAsync(cancellationToken);
    
    // 启动后台任务
    _ = BackgroundTaskAsync(cancellationToken);
}

private async Task BackgroundTaskAsync(CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            await DoWorkAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("后台任务异常", ex);
        }
    }
}
```

### 4. 配置验证

```csharp
public Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken)
{
    _ctx = context;
    
    // 验证必需配置
    var apiKey = _ctx.Settings.Get<string>("api_key");
    if (string.IsNullOrEmpty(apiKey))
    {
        _ctx.Log.Warn("API 密钥未配置，请在设置中填写 api_key");
    }
    
    return Task.CompletedTask;
}
```

---

## 常见问题

### Q: 插件无法加载？

1. 检查 `plugin.json` 是否存在且格式正确
2. 确认 `assembly` 和 `entryType` 字段与实际代码匹配
3. 确保插件目标框架为 `net10.0`
4. 检查插件 DLL 是否在正确位置

### Q: 如何调试插件？

1. 在插件项目中添加 `launchSettings.json`：
   ```json
   {
     "profiles": {
       "MyPlugin": {
         "commandName": "Executable",
         "executablePath": "path/to/SMTCPlayer.exe"
       }
     }
   }
   ```
2. 在代码中设置断点
3. 使用 Visual Studio 的"附加到进程"功能

### Q: 插件设置存储在哪里？

插件设置存储在：
```
%LocalAppData%/SMTCPlayer/plugins-data/<plugin-id>/settings.json
```

### Q: 如何实现网络请求？

插件可以自行创建 `HttpClient`：

```csharp
private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

public async Task<string> GetDataAsync(string url)
{
    using var resp = await _http.GetAsync(url);
    resp.EnsureSuccessStatusCode();
    return await resp.Content.ReadAsStringAsync();
}
```

### Q: 如何访问本机 API？

通过 `IPluginContext.ServerUrl` 访问本机后端 API（回环免认证）：

```csharp
var response = await _http.GetAsync($"{_ctx.ServerUrl}/api/status");
```

### Q: 插件支持热更新吗？

是的，插件系统使用可收集的 AssemblyLoadContext，支持：
- 运行时启用/禁用插件
- 动态加载新插件
- 卸载后重新加载更新版本

### Q: 如何处理长时间运行的任务？

使用异步任务和 CancellationToken：

```csharp
private CancellationTokenSource? _cts;

public Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken)
{
    _cts = new CancellationTokenSource();
    _ = LongRunningTaskAsync(_cts.Token);
    return Task.CompletedTask;
}

public Task OnUnloadingAsync(CancellationToken cancellationToken)
{
    _cts?.Cancel();
    return Task.CompletedTask;
}

private async Task LongRunningTaskAsync(CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await DoWorkAsync();
        await Task.Delay(1000, cancellationToken);
    }
}
```

---

## 参考示例

项目内置了两个完整插件供参考：

1. **网易云增强插件** (`plugins/SMTCPlayer.Plugins.NeteaseEnhance/`)
   - 实现 `IPlugin` + `ISearchProvider`
   - 托管子进程（NeteaseWatcher）
   - 使用隔离设置存储
   - 事件广播处理

2. **Spotify 支持插件** (`plugins/SMTCPlayer.Plugins.SpotifySupport/`)
   - 实现 `IPlugin` + `ISearchProvider`
   - FileSystemWatcher 监听命令文件
   - 外部 API 集成（Spotify Web API）

---

## 许可证

插件开发遵循 MIT 许可证。详见 [LICENSE](LICENSE) 文件。

---

## 获取帮助

- 官网：[splay.asia](https://splay.asia)
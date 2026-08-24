using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Channels;
using SMTCPlayer.Core.Services;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.Core.Plugins;

/// <summary>
/// 插件宿主：负责插件的发现、加载、启用/禁用与事件广播。
///
/// 数据流：SMTC 变化 → Python 端 /api/status → MainViewModel 轮询 →
/// diff 生成 PluginEvent → 有界 Channel → 后台顺序分发 → 所有已启用插件。
/// 单个插件抛异常只记录日志，不影响其他插件与宿主。
/// </summary>
public sealed class PluginHost
{
    private readonly SmtcApiClient _api;
    private readonly PluginRegistry _registry = new();
    private readonly Channel<PluginEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<PluginRecord> _plugins = new();
    private readonly object _sync = new();
    private volatile MediaSnapshot _currentSnapshot = MediaSnapshot.Empty;
    private Task? _dispatchLoop;
    private int _shutdown;
    private int _jobPolling;
    private string? _lastProvidersSignature;

    /// <summary>搜索提供者描述（网页端下拉框用）。</summary>
    public sealed record SearchProviderInfo(string Id, string Name);

    /// <summary>
    /// 当前可用的搜索提供者（已加载、已启用且实现 ISearchProvider 的插件）。
    /// </summary>
    public IReadOnlyList<SearchProviderInfo> GetSearchProviders()
    {
        lock (_sync)
        {
            return _plugins
                .Where(p => p.Info.IsEnabled && p.Instance is ISearchProvider)
                .Select(p => new SearchProviderInfo(p.Info.Id, p.Info.Name))
                .ToList();
        }
    }

    /// <summary>
    /// 轮询 Flask 任务队列并把任务分发给对应插件（网页端发起的搜索/播放）；
    /// 顺带在提供者集合变化时上报。单飞防止重叠。
    /// </summary>
    public async Task PollServerJobsAsync()
    {
        if (Interlocked.Exchange(ref _jobPolling, 1) == 1) return;
        try
        {
            await ReportProvidersIfChangedAsync();

            var jobs = await _api.GetPendingPluginJobsAsync();
            if (jobs == null || jobs.Count == 0) return;

            foreach (var job in jobs)
            {
                object result;
                try
                {
                    result = await InvokeProviderAsync(job);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"插件任务失败 {job.Provider}/{job.Action}: {ex.Message}");
                    result = new { success = false, error = ex.Message };
                }
                await _api.PostPluginJobResultAsync(job.Id, result);
            }
        }
        catch
        {
            // 桥接异常不影响宿主
        }
        finally
        {
            Interlocked.Exchange(ref _jobPolling, 0);
        }
    }

    /// <summary>把单个任务路由到提供者插件（单插件 20 秒超时保护）。</summary>
    private async Task<object> InvokeProviderAsync(PluginJob job)
    {
        PluginRecord? record;
        lock (_sync) record = _plugins.FirstOrDefault(p => p.Info.Id == job.Provider);

        if (record?.Instance is not ISearchProvider provider || !record.Info.IsEnabled)
        {
            throw new InvalidOperationException($"搜索提供者不可用: {job.Provider}");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var task = job.Action switch
        {
            "search" => SearchCoreAsync(provider, job.Query ?? "", cts.Token),
            "play" => PlayCoreAsync(provider, job.Item!, cts.Token),
            _ => Task.FromResult<object>(new { success = false, error = $"未知任务类型: {job.Action}" }),
        };

        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
        if (completed != task) throw new TimeoutException("插件响应超时（20 秒）");
        return await task;
    }

    private static async Task<object> SearchCoreAsync(ISearchProvider provider, string query, CancellationToken ct)
    {
        var items = await provider.SearchAsync(query, ct);
        // 网页端负载使用小写键（与前端 song-item 模板一致）
        var results = items.Select(i => new
        {
            id = i.Id,
            name = i.Name,
            artists = i.Artists,
            album = i.Album,
            cover = i.Cover,
            duration = i.Duration,
        }).ToList();
        return new { success = true, results };
    }

    private static async Task<object> PlayCoreAsync(ISearchProvider provider, SearchResultItem item, CancellationToken ct)
    {
        var ok = await provider.PlayAsync(item, ct);
        return new { success = ok, error = ok ? null : "播放失败，请确认对应音乐软件已打开" };
    }

    /// <summary>提供者集合变化时上报给 Flask（供网页端渲染下拉框）。</summary>
    private async Task ReportProvidersIfChangedAsync()
    {
        var providers = GetSearchProviders();
        var payload = providers.Select(p => new { id = p.Id, name = p.Name }).ToList();
        var signature = string.Join("|", payload.Select(p => $"{p.id}:{p.name}"));
        if (signature == _lastProvidersSignature) return;

        if (await _api.ReportPluginProvidersAsync(new { providers = payload }))
        {
            _lastProvidersSignature = signature;
            Logger.Info($"搜索提供者已上报: {(payload.Count > 0 ? signature.Replace("|", ", ") : "（无）")}");
        }
    }

    /// <summary>当前媒体快照（供 IPluginContext.Current 读取）。</summary>
    public MediaSnapshot CurrentSnapshot => _currentSnapshot;

    /// <summary>
    /// 聚合所有已加载且启用插件声明的内容服务能力（去重、小写规范化）。
    /// 空能力返回空序列；用于上报后端供网页端显隐功能。
    /// </summary>
    public IReadOnlyCollection<string> GetActiveCapabilities()
    {
        lock (_sync)
        {
            return _plugins
                .Where(p => p.Info.IsEnabled && p.Instance != null)
                .SelectMany(p => p.Info.Capabilities)
                .Select(c => c.Trim().ToLowerInvariant())
                .Where(c => c.Length > 0)
                .Distinct()
                .ToList();
        }
    }

    /// <summary>已发现的插件列表（含禁用与加载失败的）。</summary>
    public IReadOnlyList<PluginInfo> Plugins
    {
        get { lock (_sync) return _plugins.Select(p => p.Info).ToList(); }
    }

    public PluginHost(SmtcApiClient api)
    {
        _api = api;
        _channel = Channel.CreateBounded<PluginEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 插件处理过慢时丢弃最旧事件，保护宿主
            SingleReader = true,
        });
        // 分发循环先启动，之后加载的插件自动开始接收事件
        _dispatchLoop = Task.Run(DispatchLoopAsync);
    }

    // ============== 发现与加载 ==============

    /// <summary>扫描插件目录并加载所有启用的插件。单个插件失败不影响其他插件。</summary>
    public Task InitializeAsync() => RescanAsync();

    /// <summary>
    /// 重新扫描插件目录：只加载尚未发现的新插件（已加载的保持不动，支持运行期放入新插件后刷新）。
    /// 返回本次新发现的插件数量。
    /// </summary>
    public async Task<int> RescanAsync()
    {
        var added = 0;
        try
        {
            var dirs = SearchDirectories().Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var root in dirs)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        if (await LoadFromDirectoryAsync(dir)) added++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"扫描插件目录失败 {dir}: {ex.Message}");
                    }
                }
            }

            if (added > 0)
            {
                // 提供者集合可能变化，置空签名触发下次轮询重新上报
                _lastProvidersSignature = null;
                Logger.Info($"插件重扫完成: 新增 {added} 个，当前共 {Plugins.Count} 个");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("PluginHost.RescanAsync 异常", ex);
        }
        return added;
    }

    /// <summary>用户自装插件根目录（%LocalAppData%\SMTCPlayer\plugins，更新应用不丢失）。</summary>
    public static string UserPluginsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMTCPlayer", "plugins");

    private static IEnumerable<string> SearchDirectories()
    {
        // 1) exe 旁 plugins\（安装版/便携）
        yield return Path.Combine(AppContext.BaseDirectory, "plugins");
        // 2) %LocalAppData%\SMTCPlayer\plugins（用户自装，更新不丢失）
        yield return UserPluginsRoot;
    }

    /// <returns>true 表示发现并登记了新插件；false 表示无清单、清单无效或 Id 重复。</returns>
    private async Task<bool> LoadFromDirectoryAsync(string dir)
    {
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath)) return false;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifestPath));
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.Assembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            Logger.Warn($"插件清单无效，已跳过: {manifestPath}");
            return false;
        }

        PluginRecord? existing;
        lock (_sync) existing = _plugins.FirstOrDefault(p => p.Info.Id == manifest.Id);
        if (existing != null)
        {
            // 同 Id 已登记：内容有变化则热更新（卸载→重新激活），否则忽略
            return await ReloadIfChangedAsync(existing, manifest, dir);
        }

        var info = new PluginInfo
        {
            Id = manifest.Id,
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
            Version = manifest.Version,
            Author = manifest.Author,
            Description = manifest.Description,
            Directory = dir,
            IsEnabled = _registry.IsEnabled(manifest.Id),
            Capabilities = manifest.Capabilities
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        var record = new PluginRecord { Info = info };
        lock (_sync) _plugins.Add(record);

        if (!info.IsEnabled)
        {
            Logger.Info($"插件已禁用，跳过加载: {info.Id}");
            return true;
        }

        await ActivateAsync(record, manifest);
        return true;
    }

    /// <summary>实例化插件并调用 OnLoadedAsync。</summary>
    private async Task ActivateAsync(PluginRecord record, PluginManifest manifest)
    {
        // 版本门槛校验：不满足则拒绝加载并给出可读错误（缺省字段宽容放行，兼容旧插件清单）
        if (manifest.ApiVersion is int requiredApi && requiredApi > PluginApiVersion.Current)
        {
            record.Info.IsLoaded = false;
            record.Info.Error = $"需要插件 API v{requiredApi}，当前应用为 v{PluginApiVersion.Current}，请升级应用";
            Logger.Warn($"插件版本不兼容，已拒绝加载: {record.Info.Id} → {record.Info.Error}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion)
            && Version.TryParse(manifest.MinHostVersion.TrimStart('v', 'V'), out var minHost)
            && HostVersion < minHost)
        {
            record.Info.IsLoaded = false;
            record.Info.Error = $"需要宿主 v{minHost} 或更高，当前为 v{HostVersion}";
            Logger.Warn($"插件版本不兼容，已拒绝加载: {record.Info.Id} → {record.Info.Error}");
            return;
        }

        PluginLoadContext? alc = null;
        try
        {
            alc = new PluginLoadContext(record.Info.Id, record.Info.Directory);
            record.Alc = alc;
            var asmPath = Path.Combine(record.Info.Directory, manifest.Assembly);
            if (!File.Exists(asmPath)) throw new FileNotFoundException($"插件程序集不存在: {manifest.Assembly}");

            var assembly = alc.LoadFromAssemblyPath(asmPath);
            var type = assembly.GetType(manifest.EntryType, throwOnError: false)
                       ?? throw new TypeLoadException($"未找到入口类型 {manifest.EntryType}");
            if (!typeof(IPlugin).IsAssignableFrom(type))
                throw new InvalidCastException($"{manifest.EntryType} 未实现 IPlugin");

            var instance = (IPlugin)Activator.CreateInstance(type)!;
            var context = new PluginContextImpl(this, record.Info.Id, _api);

            await instance.OnLoadedAsync(context, _cts.Token);

            record.Instance = instance;
            record.Context = context;
            record.AssemblyStampUtc = File.GetLastWriteTimeUtc(asmPath);
            record.Info.IsLoaded = true;
            record.Info.Error = null;
            Logger.Info($"插件已加载: {record.Info.Id} v{record.Info.Version} ({record.Info.Name})");
        }
        catch (Exception ex)
        {
            // 失败也要释放隔离上下文，避免孤儿 ALC 持有 DLL
            try { alc?.Unload(); } catch { /* 忽略 */ }
            if (record.Alc == alc) record.Alc = null;
            record.Info.IsLoaded = false;
            record.Info.Error = ex.Message;
            Logger.Error($"插件加载失败: {record.Info.Id}", ex);
        }
    }

    // ============== 启用 / 禁用 ==============

    /// <summary>
    /// 卸载单个插件：通知 OnUnloadingAsync（最多等 3 秒）→ 丢弃宿主侧引用 →
    /// Unload 可收集 ALC → 后台验证程序集回收。
    /// 插件不得静态持有宿主对象，否则卸载会退化为"进程退出时才释放"（不影响正确性）。
    /// </summary>
    private async Task UnloadPluginAsync(PluginRecord record)
    {
        var instance = record.Instance;
        record.Instance = null;
        record.Context = null;

        if (instance != null)
        {
            try
            {
                var task = instance.OnUnloadingAsync(_cts.Token);
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3))); // 单插件最多等 3 秒
                Logger.Info($"插件已卸载: {record.Info.Id}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"插件 {record.Info.Id} 卸载异常: {ex.Message}");
            }
        }

        var alc = record.Alc;
        record.Alc = null;
        record.Info.IsLoaded = false;
        if (alc == null) return;

        try
        {
            alc.Unload();
        }
        catch (Exception ex)
        {
            Logger.Warn($"插件 {record.Info.Id} ALC Unload 失败: {ex.Message}");
            return;
        }

        var weak = new WeakReference(alc);
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 5 && weak.IsAlive; i++)
            {
                await Task.Delay(200);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            if (weak.IsAlive)
                Logger.Debug($"插件 {record.Info.Id} 程序集暂未回收（存在外部引用），将在下次 GC 释放");
            else
                Logger.Debug($"插件 {record.Info.Id} 程序集已卸载并回收");
        });
    }

    /// <summary>已登记的同 Id 插件：检测到内容变化时热更新（卸载后用新目录重新激活）。返回是否发生变更。</summary>
    private async Task<bool> ReloadIfChangedAsync(PluginRecord existing, PluginManifest manifest, string dir)
    {
        bool changed;
        var asmPath = Path.Combine(dir, manifest.Assembly);
        try
        {
            changed = File.Exists(asmPath)
                && (!string.Equals(existing.Info.Directory, dir, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.Info.Version, manifest.Version, StringComparison.OrdinalIgnoreCase)
                    || existing.AssemblyStampUtc == default
                    || File.GetLastWriteTimeUtc(asmPath) != existing.AssemblyStampUtc);
        }
        catch
        {
            return false;
        }
        if (!changed) return false;

        Logger.Info($"检测到插件更新: {existing.Info.Id} v{existing.Info.Version} → v{manifest.Version}，执行热更新");

        await UnloadPluginAsync(existing);
        _lastProvidersSignature = null;

        // 原记录复用（保持列表顺序与 UI 稳定），刷新元数据指向新目录/版本
        existing.Info.Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name;
        existing.Info.Version = manifest.Version;
        existing.Info.Author = manifest.Author;
        existing.Info.Description = manifest.Description;
        existing.Info.Directory = dir;
        existing.Info.Capabilities = NormalizeCapabilities(manifest);
        existing.Info.Error = null;
        existing.Info.IsEnabled = _registry.IsEnabled(manifest.Id);

        if (!existing.Info.IsEnabled)
        {
            Logger.Info($"插件更新后处于禁用状态，跳过加载: {existing.Info.Id}");
            return true;
        }

        await ActivateAsync(existing, manifest);
        return true;
    }

    private static List<string> NormalizeCapabilities(PluginManifest manifest) =>
        manifest.Capabilities
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 运行时切换启用状态：禁用立即停止事件分发并卸载程序集；重新启用时全新加载。
    /// 状态持久化到 plugins.json，重启后生效加载/跳过。
    /// </summary>
    public async Task SetPluginEnabledAsync(string pluginId, bool enabled)
    {
        PluginRecord? record;
        lock (_sync) record = _plugins.FirstOrDefault(p => p.Info.Id == pluginId);
        if (record == null) return;

        _registry.SetEnabled(pluginId, enabled);
        record.Info.IsEnabled = enabled;

        // 插件生命周期一律放到线程池执行：
        // 1) 隔离插件的 SynchronizationContext 捕获，杜绝其内部 await 续体回流 UI 线程
        //    与宿主同步等待互相卡死；2) 插件内的阻塞操作（如 Kill+WaitForExit）不再冻结 UI。
        if (enabled && record.Instance == null && record.Alc == null)
        {
            // 重新启用：读取 manifest 并实例化（全新 ALC）
            var manifestPath = Path.Combine(record.Info.Directory, "plugin.json");
            var manifest = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifestPath));
            if (manifest != null) await Task.Run(() => ActivateAsync(record, manifest));
        }
        else if (!enabled && record.Instance != null)
        {
            await Task.Run(() => UnloadPluginAsync(record));
            _lastProvidersSignature = null;
            Logger.Info($"插件已禁用并释放程序集: {pluginId}");
        }

        Logger.Info($"插件 {(enabled ? "已启用" : "已禁用")}: {pluginId}");
    }

    // ============== 事件广播 ==============

    /// <summary>更新插件可见的当前快照（每次轮询调用）。</summary>
    public void UpdateSnapshot(MediaSnapshot snapshot) => _currentSnapshot = snapshot;

    /// <summary>把 diff 产生的事件写入广播队列（非阻塞）。</summary>
    public void Publish(IEnumerable<PluginEvent> events)
    {
        foreach (var evt in events)
        {
            _channel.Writer.TryWrite(evt);
        }
    }

    /// <summary>广播服务启停事件。</summary>
    public void PublishServerState(bool isRunning)
    {
        _channel.Writer.TryWrite(new PluginEvent
        {
            Type = PluginEventType.ServerStateChanged,
            IsServerRunning = isRunning,
        });
    }

    /// <summary>后台分发循环：单读者顺序分发，保证事件顺序；异常按插件隔离。</summary>
    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                List<PluginRecord> snapshot;
                lock (_sync) snapshot = _plugins.ToList();

                foreach (var plugin in snapshot)
                {
                    if (!plugin.Info.IsEnabled || plugin.Instance == null) continue;
                    try
                    {
                        await plugin.Instance.HandleEventAsync(evt, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"插件 {plugin.Info.Id} 处理 {evt.Type} 事件异常: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            Logger.Error("插件分发循环异常退出", ex);
        }
    }

    // ============== 关闭 ==============

    /// <summary>通知所有已加载插件 OnUnloading 并停止分发（应用退出时调用，幂等）。</summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) == 1) return;

        List<PluginRecord> snapshot;
        lock (_sync) snapshot = _plugins.ToList();

        foreach (var plugin in snapshot)
        {
            if (plugin.Instance == null) continue;
            try
            {
                var task = plugin.Instance.OnUnloadingAsync(_cts.Token);
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3))); // 单插件最多等 3 秒
                Logger.Info($"插件已卸载: {plugin.Info.Id}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"插件 {plugin.Info.Id} 卸载异常: {ex.Message}");
            }
        }

        _cts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            if (_dispatchLoop != null) await Task.WhenAny(_dispatchLoop, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        catch { }
    }

    // ============== 内部类型 ==============

    /// <summary>宿主应用版本（取入口 exe 的程序集版本，失败回落 Core 自身）。</summary>
    private static Version HostVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0);

    private sealed class PluginRecord
    {
        public PluginInfo Info { get; init; } = null!;
        public IPlugin? Instance { get; set; }
        public PluginContextImpl? Context { get; set; }

        /// <summary>插件隔离上下文（可收集），禁用/热更新时 Unload。</summary>
        public PluginLoadContext? Alc { get; set; }

        /// <summary>激活时插件主程序集的写入时间（UTC），用于重扫时检测热更新。</summary>
        public DateTime AssemblyStampUtc { get; set; }
    }

    /// <summary>
    /// 插件隔离加载上下文：优先从插件目录解析依赖；
    /// 契约程序集（SMTCPlayer.PluginApi）与运行时库回落到默认上下文，
    /// 保证宿主与插件共享同一份 IPlugin 类型。
    /// 可收集（collectible）：禁用/热更新时调用 <see cref="AssemblyLoadContext.Unload"/>，
    /// 在宿主与插件均不残留强引用的前提下程序集可被真正卸载。
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public PluginLoadContext(string pluginId, string dir)
            : base($"plugin-{pluginId}", isCollectible: true)
        {
            _dir = dir;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // 契约程序集必须与宿主共享同一份，否则 IPlugin 类型不匹配
            if (name.Name == "SMTCPlayer.PluginApi") return null;

            var path = Path.Combine(_dir, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null; // null → 回落默认上下文
        }
    }
}

using System.Reflection;
using System.Text.Json;
using SMTCPlayer.Logging;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.PluginSystem;

/// <summary>
/// 插件宿主外观：负责插件的发现、加载、启停、热更新与事件广播。
///
/// 数据流：SMTC 变化 → 轮询方 diff 生成 PluginEvent → PluginBroadcastPool（有界通道，
/// DropOldest）→ 每个已激活插件一个订阅者适配器 → 插件 HandleEventAsync。
/// 单个插件抛异常只记录日志，不影响其他插件与宿主。
/// 后端交互走 BackendBridgeClient（回环免认证）；卸载序列保证在途事件收敛后再 Unload 程序集。
/// </summary>
public sealed class PluginManager
{
    private static readonly ILog Log = LogManager.GetLogger("PluginSystem");

    private readonly BackendBridgeClient _bridge;
    private readonly PluginRegistry _registry = new();
    private readonly PluginBroadcastPool _broadcast;
    private readonly List<PluginRecord> _plugins = new();
    private readonly object _sync = new();
    private volatile MediaSnapshot _currentSnapshot = MediaSnapshot.Empty;
    private int _shutdown;
    private int _jobPolling;
    private string? _lastProvidersSignature;

    public PluginManager(string backendBaseUrl)
    {
        _bridge = new BackendBridgeClient(backendBaseUrl);
        // 分发循环随池启动，之后激活的插件自动开始接收事件
        _broadcast = new PluginBroadcastPool();
    }

    /// <summary>事件广播池（供宿主外部如 LAN 服务订阅同一事件流）。</summary>
    public PluginBroadcastPool Broadcast => _broadcast;

    /// <summary>当前媒体快照（供 IPluginContext.Current 读取）。</summary>
    public MediaSnapshot CurrentSnapshot => _currentSnapshot;

    /// <summary>已发现的插件列表（含禁用与加载失败的）。</summary>
    public IReadOnlyList<PluginInfo> Plugins
    {
        get { lock (_sync) return _plugins.Select(p => p.Info).ToList(); }
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
                        Log.Warn($"扫描插件目录失败 {dir}: {ex.Message}");
                    }
                }
            }

            if (added > 0)
            {
                // 提供者集合可能变化，置空签名触发下次轮询重新上报
                _lastProvidersSignature = null;
                Log.Info($"插件重扫完成: 新增 {added} 个，当前共 {Plugins.Count} 个");
            }
        }
        catch (Exception ex)
        {
            Log.Error("PluginManager.RescanAsync 异常", ex);
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
            Log.Warn($"插件清单无效，已跳过: {manifestPath}");
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
            Log.Info($"插件已禁用，跳过加载: {info.Id}");
            return true;
        }

        await ActivateAsync(record, manifest);
        return true;
    }

    /// <summary>实例化插件并调用 OnLoadedAsync；成功后创建插件专属令牌并注册广播订阅。</summary>
    private async Task ActivateAsync(PluginRecord record, PluginManifest manifest)
    {
        // 版本门槛校验：不满足则拒绝加载并给出可读错误（缺省字段宽容放行，兼容旧插件清单）
        if (manifest.ApiVersion is int requiredApi && requiredApi > PluginApiVersion.Current)
        {
            record.Info.IsLoaded = false;
            record.Info.Error = $"需要插件 API v{requiredApi}，当前应用为 v{PluginApiVersion.Current}，请升级应用";
            Log.Warn($"插件版本不兼容，已拒绝加载: {record.Info.Id} → {record.Info.Error}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion)
            && Version.TryParse(manifest.MinHostVersion.TrimStart('v', 'V'), out var minHost)
            && HostVersion < minHost)
        {
            record.Info.IsLoaded = false;
            record.Info.Error = $"需要宿主 v{minHost} 或更高，当前为 v{HostVersion}";
            Log.Warn($"插件版本不兼容，已拒绝加载: {record.Info.Id} → {record.Info.Error}");
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

            // 加载期主动预扫描：直接引用了宿主程序集（Core/PluginSystem/WinUI/Wpf）即当场拒绝，
            // 而不是等到运行时惰性解析某方法时才碰巧抛 FileNotFoundException。
            foreach (var referenced in assembly.GetReferencedAssemblies())
            {
                if (PluginLoadContext.IsForbiddenHostAssembly(referenced.Name))
                    throw new PluginLoadException(
                        $"插件 '{record.Info.Id}' 引用了禁止的宿主程序集 '{referenced.Name}'（插件只能依赖 SMTCPlayer.PluginApi 与自带依赖）");
            }

            var type = assembly.GetType(manifest.EntryType, throwOnError: false)
                       ?? throw new TypeLoadException($"未找到入口类型 {manifest.EntryType}");
            if (!typeof(IPlugin).IsAssignableFrom(type))
                throw new InvalidCastException($"{manifest.EntryType} 未实现 IPlugin");

            var instance = (IPlugin)Activator.CreateInstance(type)!;
            var context = new PluginContextImpl(this, record.Info.Id, _bridge);

            // 插件专属令牌先建（OnLoadedAsync 与后续事件处理共用；失败路径在 catch 统一释放）
            record.OwnCts = new CancellationTokenSource();
            await instance.OnLoadedAsync(context, record.OwnCts.Token);

            record.Instance = instance;
            record.Context = context;
            // 激活成功后才注册广播订阅：此后池内事件开始路由到该插件
            record.PoolSubscription = _broadcast.Subscribe(new PluginSubscriber(record));
            record.AssemblyStampUtc = File.GetLastWriteTimeUtc(asmPath);
            record.Info.IsLoaded = true;
            record.Info.Error = null;
            Log.Info($"插件已加载: {record.Info.Id} v{record.Info.Version} ({record.Info.Name})");
        }
        catch (Exception ex)
        {
            // 失败也要释放半成品生命周期对象与隔离上下文，避免孤儿 ALC 持有 DLL
            try { record.PoolSubscription?.Dispose(); } catch { /* 忽略 */ }
            record.PoolSubscription = null;
            var ownCts = record.OwnCts;
            if (record.OwnCts == ownCts) record.OwnCts = null;
            try { ownCts?.Cancel(); ownCts?.Dispose(); } catch { /* 忽略 */ }
            try { alc?.Unload(); } catch { /* 忽略 */ }
            if (record.Alc == alc) record.Alc = null;
            record.Instance = null;
            record.Context = null;
            record.Info.IsLoaded = false;
            // 基异常消息透出：黑名单等被包装的深层异常才能看到可读文案，同时保留外层消息拼接
            var baseMsg = ex.GetBaseException().Message;
            record.Info.Error = ex.Message == baseMsg ? ex.Message : $"{ex.Message} → {baseMsg}";
            Log.Error($"插件加载失败: {record.Info.Id}", ex);
        }
    }

    // ============== 启用 / 禁用 ==============

    /// <summary>
    /// 卸载单个插件（严格顺序）：
    /// ① 摘除分发名单（订阅快照机制保证后续事件不再路由给它）并丢弃宿主侧引用 →
    /// ② 取消插件专属令牌（在途 HandleEventAsync 尽快感知）→
    /// ③ 等待在途事件处理归零（上限 3 秒，超时记 Warn 继续）→
    /// ④ 通知 OnUnloadingAsync（最多等 3 秒）→
    /// ⑤ 释放令牌、Unload 可收集 ALC → 后台验证程序集回收。
    /// 插件不得静态持有宿主对象，否则卸载会退化为"进程退出时才释放"（不影响正确性）。
    /// </summary>
    private async Task UnloadPluginAsync(PluginRecord record)
    {
        // ① 摘除分发名单 + 丢弃宿主侧引用
        var instance = record.Instance;
        var ownCts = record.OwnCts;
        try { record.PoolSubscription?.Dispose(); } catch { /* 退订永不抛 */ }
        record.PoolSubscription = null;
        record.Instance = null;
        record.Context = null;

        // ② 取消插件专属令牌
        try { ownCts?.Cancel(); } catch { /* 取消永不抛 */ }

        // ③ 等待在途事件处理归零（上限 3 秒）
        var deadline = Environment.TickCount64 + 3000;
        while (Volatile.Read(ref record.InFlight) != 0 && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        if (Volatile.Read(ref record.InFlight) != 0)
            Log.Warn($"插件 {record.Info.Id} 在途事件处理 3 秒内未结束，继续卸载");

        // ④ OnUnloadingAsync（单插件最多等 3 秒；令牌已取消，插件应尽快保存状态返回）
        if (instance != null)
        {
            try
            {
                var task = instance.OnUnloadingAsync(ownCts?.Token ?? CancellationToken.None);
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
                Log.Info($"插件已卸载: {record.Info.Id}");
            }
            catch (Exception ex)
            {
                Log.Warn($"插件 {record.Info.Id} 卸载异常: {ex.Message}");
            }
        }

        // ⑤ 释放令牌、Unload 可收集 ALC（并发重新激活时不动新令牌）
        record.Info.IsLoaded = false;
        try { ownCts?.Dispose(); } catch { /* 释放永不抛 */ }
        if (record.OwnCts == ownCts) record.OwnCts = null;

        var alc = record.Alc;
        record.Alc = null;
        if (alc == null) return;

        try
        {
            alc.Unload();
        }
        catch (Exception ex)
        {
            Log.Warn($"插件 {record.Info.Id} ALC Unload 失败: {ex.Message}");
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
                Log.Debug($"插件 {record.Info.Id} 程序集暂未回收（存在外部引用），将在下次 GC 释放");
            else
                Log.Debug($"插件 {record.Info.Id} 程序集已卸载并回收");
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

        Log.Info($"检测到插件更新: {existing.Info.Id} v{existing.Info.Version} → v{manifest.Version}，执行热更新");

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
            Log.Info($"插件更新后处于禁用状态，跳过加载: {existing.Info.Id}");
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
            Log.Info($"插件已禁用并释放程序集: {pluginId}");
        }

        Log.Info($"插件 {(enabled ? "已启用" : "已禁用")}: {pluginId}");
    }

    // ============== 供安装器使用 ==============

    /// <summary>
    /// 让宿主"忘记"一个已登记的插件（PluginInstaller 安装/卸载时使用）：
    /// 若仍处于加载态先走完整卸载序列（与禁用一致，但不改动 plugins.json 的启用状态），
    /// 然后从插件列表移除记录——RescanAsync 只加不减，这是移除已消失插件记录的唯一途径。
    /// removeRegistryState=true 时同时从 plugins.json 清除其禁用记录（彻底卸载，重装后恢复默认启用）。
    /// </summary>
    internal async Task ForgetPlugin(string pluginId, bool removeRegistryState = false)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;

        PluginRecord? record;
        lock (_sync) record = _plugins.FirstOrDefault(p => p.Info.Id == pluginId);
        if (record == null) return;

        // 已加载（或激活过留有 ALC）先走完整卸载序列；不动 _registry，保持用户原启用状态
        if (record.Instance != null || record.Alc != null)
        {
            try
            {
                await UnloadPluginAsync(record);
            }
            catch (Exception ex)
            {
                Log.Warn($"ForgetPlugin 卸载 {pluginId} 异常: {ex.Message}");
            }
        }

        lock (_sync) _plugins.Remove(record);
        _lastProvidersSignature = null; // 提供者集合可能变化，触发下次轮询重新上报

        // 彻底卸载：把 Id 从禁用列表摘除（等效改为"重新启用"状态并持久化，plugins.json 无残留）
        if (removeRegistryState) _registry.SetEnabled(pluginId, true);

        Log.Info($"插件已从宿主记录移除: {pluginId}");
    }

    // ============== 事件广播 ==============

    /// <summary>更新插件可见的当前快照（每次轮询调用）。</summary>
    public void UpdateSnapshot(MediaSnapshot snapshot) => _currentSnapshot = snapshot;

    /// <summary>把 diff 产生的事件写入广播池（非阻塞；池内 DropOldest 保护）。</summary>
    public void Publish(IEnumerable<PluginEvent> events) => _broadcast.Publish(events);

    /// <summary>广播服务启停事件。</summary>
    public void PublishServerState(bool isRunning)
    {
        _broadcast.Publish(new PluginEvent
        {
            Type = PluginEventType.ServerStateChanged,
            IsServerRunning = isRunning,
        });
    }

    // ============== 任务轮询与提供者上报 ==============

    /// <summary>
    /// 轮询后端任务队列并把任务分发给对应插件（网页端发起的搜索/播放）；
    /// 顺带在提供者集合变化时上报。单飞防止重叠。
    /// </summary>
    public async Task PollServerJobsAsync()
    {
        if (Interlocked.Exchange(ref _jobPolling, 1) == 1) return;
        try
        {
            await ReportProvidersIfChangedAsync();

            var jobs = await _bridge.GetPendingPluginJobsAsync();
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
                    Log.Warn($"插件任务失败 {job.Provider}/{job.Action}: {ex.Message}");
                    result = new { success = false, error = ex.Message };
                }
                await _bridge.PostPluginJobResultAsync(job.Id, result);
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

    /// <summary>提供者集合变化时上报给后端（供网页端渲染下拉框）。</summary>
    private async Task ReportProvidersIfChangedAsync()
    {
        var providers = GetSearchProviders();
        var payload = providers.Select(p => new { id = p.Id, name = p.Name }).ToList();
        var signature = string.Join("|", payload.Select(p => $"{p.id}:{p.name}"));
        if (signature == _lastProvidersSignature) return;

        if (await _bridge.ReportPluginProvidersAsync(new { providers = payload }))
        {
            _lastProvidersSignature = signature;
            Log.Info($"搜索提供者已上报: {(payload.Count > 0 ? signature.Replace("|", ", ") : "（无）")}");
        }
    }

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

    // ============== 关闭 ==============

    /// <summary>对所有已加载插件执行完整卸载序列，然后关闭广播池（应用退出时调用，幂等）。</summary>
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
                await UnloadPluginAsync(plugin);
            }
            catch (Exception ex)
            {
                Log.Warn($"插件 {plugin.Info.Id} 卸载异常: {ex.Message}");
            }
        }

        // 池内部限时（3 秒）排空分发循环
        await _broadcast.DisposeAsync();
    }

    // ============== 内部类型 ==============

    /// <summary>宿主应用版本（取入口 exe 的程序集版本，失败回落本程序集）。</summary>
    private static Version HostVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0);

    /// <summary>插件运行时记录（宿主侧对每个已发现插件的唯一档案）。</summary>
    internal sealed class PluginRecord
    {
        public PluginInfo Info { get; init; } = null!;
        public IPlugin? Instance { get; set; }
        public PluginContextImpl? Context { get; set; }

        /// <summary>插件隔离上下文（可收集），禁用/热更新时 Unload。</summary>
        public PluginLoadContext? Alc { get; set; }

        /// <summary>激活时插件主程序集的写入时间（UTC），用于重扫时检测热更新。</summary>
        public DateTime AssemblyStampUtc { get; set; }

        /// <summary>插件专属取消令牌：卸载时先取消，让在途事件处理与 OnUnloadingAsync 尽快收敛。</summary>
        public CancellationTokenSource? OwnCts { get; set; }

        /// <summary>在途事件处理计数（Interlocked 维护），卸载前等待归零。</summary>
        public int InFlight;

        /// <summary>广播池订阅句柄：卸载第一步 Dispose，后续事件不再路由给该插件。</summary>
        public IDisposable? PoolSubscription { get; set; }
    }
}

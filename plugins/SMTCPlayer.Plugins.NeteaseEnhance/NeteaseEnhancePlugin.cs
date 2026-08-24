using System.Diagnostics;
using System.Reflection;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.Plugins.NeteaseEnhance;

/// <summary>
/// 示例插件：网易云增强（从主程序剥离的原内置功能）。
/// 演示：事件广播（ServerStateChanged/SongChanged）、IPluginContext.ServerUrl、
/// 插件自建 HttpClient 访问本机 API（回环免认证）、隔离设置与插件日志、
/// ISearchProvider（承接网页端的搜索与播放）。
///
/// 功能：
/// 1. 服务运行期间定期查询网易云登录状态，登录/登出/昵称变化写日志；
/// 2. 可选：应用退出时自动清除网易云 Cookie（settings: clear_cookies_on_exit，默认关闭）；
/// 3. 搜索提供者：网页端搜索/播放请求经宿主路由到此，回环调用本机 /api/ncm/* 端点；
/// 4. 托管"网易云状态监视器"子进程（原创 Python 实现 NeteaseWatcher，
///    提供比系统 SMTC 更精准的播放进度与曲目元数据；settings: watcher_enabled，默认开启）。
/// </summary>
public sealed class NeteaseEnhancePlugin : IPlugin, ISearchProvider
{
    private IPluginContext _ctx = null!;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private Timer? _statusTimer;
    private bool _known;            // 是否已建立登录状态基线
    private bool _lastLoggedIn;
    private string _lastNickname = "";
    private Process? _watcherProc;

    public Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        var clearOnExit = _ctx.Settings.Get("clear_cookies_on_exit", false);
        _ctx.Log.Info($"已加载（检查间隔 {_ctx.Settings.Get("check_interval_seconds", 60)}s，退出清 Cookie: {(clearOnExit ? "开" : "关")}，状态监视器: {(_ctx.Settings.Get("watcher_enabled", true) ? "开" : "关")}）");

        // 插件可能在服务启动后才被启用：主动探测一次，服务已在运行则立即接管监视器生命周期
        _ = ProbeServerAndStartAsync();
        return Task.CompletedTask;
    }

    private async Task ProbeServerAndStartAsync()
    {
        try
        {
            using var resp = await _http.GetAsync($"{_ctx.ServerUrl}/api/health");
            if (resp.IsSuccessStatusCode)
                await StartWatchingAsync();
        }
        catch
        {
            // 服务尚未启动：等待 ServerStateChanged 事件
        }
    }

    public Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
    {
        switch (pluginEvent.Type)
        {
            case PluginEventType.ServerStateChanged:
                // 事件分发循环上异步执行；失败仅记日志，不阻塞其他插件
                if (pluginEvent.IsServerRunning)
                    _ = StartWatchingAsync();
                else
                    StopWatching();
                break;

            case PluginEventType.SongChanged:
                if (pluginEvent.After?.SongId is long songId)
                    _ctx.Log.Debug($"网易云曲目 ID: {songId}");
                break;
        }
        return Task.CompletedTask;
    }

    public async Task OnUnloadingAsync(CancellationToken cancellationToken)
    {
        StopWatching();
        StopWatcherProcess();

        // 关闭序列中插件卸载先于后端服务停止，此时服务仍在运行，可以调用 API
        if (_ctx.Settings.Get("clear_cookies_on_exit", false))
        {
            try
            {
                using var resp = await _http.PostAsync($"{_ctx.ServerUrl}/api/ncm/clear_cookies", content: null, cancellationToken);
                if (resp.IsSuccessStatusCode)
                    _ctx.Log.Info("已按设置清除网易云 Cookie");
                else
                    _ctx.Log.Warn($"清除网易云 Cookie 失败: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                _ctx.Log.Warn($"清除网易云 Cookie 失败: {ex.Message}");
            }
        }

        _http.Dispose();
    }

    // ============== 登录状态监控 ==============

    private async Task StartWatchingAsync()
    {
        StopWatching();
        if (_ctx.Settings.Get("watcher_enabled", true))
            await StartWatcherProcessAsync();
        var interval = Math.Clamp(_ctx.Settings.Get("check_interval_seconds", 60), 10, 3600);
        _statusTimer = new Timer(
            async _ => await CheckStatusAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(interval));
    }

    private void StopWatching()
    {
        _statusTimer?.Dispose();
        _statusTimer = null;
        _known = false; // 服务重启后重新建立基线
        StopWatcherProcess();
    }

    private async Task CheckStatusAsync()
    {
        try
        {
            using var resp = await _http.GetAsync($"{_ctx.ServerUrl}/api/ncm/status");
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("logged_in", out var loggedInEl) ||
                loggedInEl.ValueKind != JsonValueKind.True && loggedInEl.ValueKind != JsonValueKind.False)
                return;

            var loggedIn = loggedInEl.GetBoolean();
            var nickname = root.TryGetProperty("nickname", out var nickEl) && nickEl.ValueKind == JsonValueKind.String
                ? nickEl.GetString() ?? ""
                : "";

            if (!_known)
            {
                _known = true;
                _lastLoggedIn = loggedIn;
                _lastNickname = nickname;
                _ctx.Log.Info(loggedIn ? $"网易云已登录: {nickname}" : "网易云未登录");
            }
            else if (loggedIn != _lastLoggedIn || nickname != _lastNickname)
            {
                _lastLoggedIn = loggedIn;
                _lastNickname = nickname;
                _ctx.Log.Info(loggedIn ? $"网易云账号已登录: {nickname}" : "网易云账号已退出登录");
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Debug($"查询网易云状态失败: {ex.Message}");
        }
    }

    // ============== 状态监视器子进程托管（原创 Python 实现 NeteaseWatcher） ==============

    /// <summary>监视器 exe 位于插件目录自带资源 plugins\netease-enhance\watcher\ 下。</summary>
    private static string? LocateFrozenWatcher()
    {
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(assemblyDir)) return null;
        var candidate = Path.Combine(assemblyDir, "watcher", "NeteaseWatcher.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>启动监视器子进程。优先用插件自带的独立 exe，开发环境回落 python 脚本。
    /// 全异步实现：严禁在 UI 线程同步等待（曾因 GetResult() 导致启用插件时整应用卡死）。</summary>
    private async Task StartWatcherProcessAsync()
    {
        if (_watcherProc is { HasExited: false })
            return;

        // 先清剿历史残留的同名监视器进程：
        // 旧实例可能带着缺陷扫描器占住 3574，导致新实例待机、时间戳永远缺失。
        CullStaleWatchers();

        // 固定端口独占策略：若仍有可用实例（如刚被其他组件拉起）则复用
        if (await IsWatcherAliveAsync())
        {
            _ctx.Log.Info("检测到 3574 端口已有状态监视器实例，复用之");
            return;
        }

        var appDir = AppContext.BaseDirectory;
        var serverDir = Path.Combine(appDir, "server");
        var scriptPath = Path.Combine(serverDir, "netease_watcher_server.py");

        string fileName, arguments, workingDir;
        var frozenExe = LocateFrozenWatcher();
        if (frozenExe != null)
        {
            // 插件自带的独立可执行（无需系统 Python）
            (fileName, arguments, workingDir) = (frozenExe, "", Path.GetDirectoryName(frozenExe)!);
        }
        else if (File.Exists(scriptPath))
        {
            // 开发环境：用系统 Python 运行脚本
            (fileName, arguments, workingDir) = ("python", $"\"{scriptPath}\"", serverDir);
        }
        else
        {
            _ctx.Log.Debug("未找到状态监视器（NeteaseWatcher），跳过启动");
            return;
        }

        try
        {
            _watcherProc = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });
            if (_watcherProc == null)
            {
                _ctx.Log.Warn("状态监视器进程启动失败");
                return;
            }
            _watcherProc.EnableRaisingEvents = true;
            // 兜底：主进程崩溃/强杀时 OS 自动终结监视器，杜绝孤儿进程
            try { WatcherJob.Attach(_watcherProc); } catch { /* 仅失去兜底 */ }
            _watcherProc.Exited += (_, _) =>
            {
                // 端口被占/异常退出时静默结束；端口冲突场景旧实例仍在服务，功能不受影响
                _ctx.Log.Debug($"状态监视器进程退出 (code: {_watcherProc.ExitCode})");
            };

            // 转发日志到插件日志，便于诊断（子进程已带 [Watcher] 前缀时不重复添加）
            _watcherProc.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                _ctx.Log.Info(e.Data.StartsWith("[Watcher]") ? e.Data : $"[Watcher] {e.Data}");
            };
            _watcherProc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _ctx.Log.Debug($"[Watcher] {e.Data}"); };
            _watcherProc.BeginOutputReadLine();
            _watcherProc.BeginErrorReadLine();

            _ctx.Log.Info($"状态监视器已启动 (PID {_watcherProc.Id}, {(frozenExe != null ? "插件自带 exe" : "python")})");
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"状态监视器启动失败: {ex.Message}");
        }
    }

    /// <summary>结束所有非本插件管理的监视器进程（新旧名称都算）。
    /// 进程名已被本项目独占，按名清剿是安全的。</summary>
    private void CullStaleWatchers()
    {
        int killed = 0;
        foreach (var name in new[] { "NeteaseWatcher", "netease-watcher" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (_watcherProc != null && p.Id == _watcherProc.Id) continue;
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
                catch { /* 已退出或权限不足 */ }
                finally { p.Dispose(); }
            }
        }
        if (killed > 0)
        {
            _ctx.Log.Info($"已清理 {killed} 个残留状态监视器进程");
            Thread.Sleep(300); // 等待端口释放
        }
    }

    /// <summary>探测 3574 端口是否已有可用的监视器实例（2 秒超时）。
    /// 用 CancellationToken 取消而非 WaitAsync 遗弃：避免底层任务稍后的
    /// 连接拒绝异常成为 Unobserved Task 被全局处理器反复记录。</summary>
    private async Task<bool> IsWatcherAliveAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var resp = await _http.GetAsync("http://127.0.0.1:3574/", cts.Token);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("time", out _);
        }
        catch
        {
            // 连接拒绝 / 超时取消：均视为"无实例"
            return false;
        }
    }

    private void StopWatcherProcess()
    {
        try
        {
            if (_watcherProc is { HasExited: false })
            {
                _watcherProc.Kill(entireProcessTree: true);
                _watcherProc.WaitForExit(2000);
                _ctx.Log.Info("状态监视器已停止");
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Debug($"停止状态监视器异常: {ex.Message}");
        }
        finally
        {
            _watcherProc?.Dispose();
            _watcherProc = null;
        }
    }

    // ============== 搜索提供者（网页端搜索/播放经宿主路由到此处） ==============

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"{_ctx.ServerUrl}/api/ncm/search?q={Uri.EscapeDataString(query)}&limit=30";
        using var resp = await _http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException(errEl.GetString());

        var items = new List<SearchResultItem>();
        if (!root.TryGetProperty("songs", out var songsEl) || songsEl.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var s in songsEl.EnumerateArray())
        {
            if (!s.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;

            items.Add(new SearchResultItem
            {
                Id = id,
                Name = GetStr(s, "name"),
                Artists = GetStr(s, "artists"),
                Album = GetStr(s, "album"),
                Cover = GetStr(s, "cover"),
                Duration = s.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                    ? dEl.GetDouble()
                    : 0,
            });
        }
        return items;
    }

    public async Task<bool> PlayAsync(SearchResultItem item, CancellationToken cancellationToken)
    {
        if (!long.TryParse(item.Id, out var songId))
            throw new InvalidOperationException($"无效的网易云曲目 ID: {item.Id}");

        using var content = new StringContent(
            JsonSerializer.Serialize(new { song_id = songId }), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_ctx.ServerUrl}/api/ncm/play", content, cancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("success", out var okEl) && okEl.ValueKind == JsonValueKind.True)
            return true;
        if (doc.RootElement.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            _ctx.Log.Warn($"网易云播放失败: {errEl.GetString()}");
        return false;
    }

    private static string GetStr(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
}

// ============== 子进程兜底作业对象（自包含，不依赖 Core） ==============

/// <summary>把监视器子进程挂到宿主 Job（KILL_ON_JOB_CLOSE）：主进程任何形式的
/// 终止都会由操作系统保证子进程随之退出。</summary>
internal static class WatcherJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
        ref ExtendedLimitInformation lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private static IntPtr _handle;

    public static void Attach(Process child)
    {
        if (_handle == IntPtr.Zero)
        {
            _handle = CreateJobObjectW(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) return;
            var info = new ExtendedLimitInformation();
            info.Basic.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation,
                    ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                _handle = IntPtr.Zero;
                return;
            }
        }
        AssignProcessToJobObject(_handle, child.Handle);
    }
}

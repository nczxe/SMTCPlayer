using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.Plugins.SpotifySupport;

/// <summary>
/// 示例插件：Spotify 支持。
/// 1. SMTC 基础功能：宿主轮询/控制天然覆盖 Spotify 会话，本插件识别 Spotify 来源并记录切歌/播放状态；
/// 2. 搜索提供者（ISearchProvider）：网页端搜索/播放请求经宿主路由到此，
///    搜索用官方 Web API（Client Credentials），播放通过 Spotify.exe --uri 在已打开的客户端中播放；
/// 3. 命令文件 play-request.json：支持 {"query":"..."}（搜索取首个曲目）或 {"uri":"spotify:track:..."}。
/// 凭证配置：%LocalAppData%/SMTCPlayer/plugins-data/spotify-support/settings.json
/// 的 client_id / client_secret（https://developer.spotify.com/dashboard 创建应用获取）。
/// </summary>
public sealed class SpotifySupportPlugin : IPlugin, ISearchProvider
{
    private IPluginContext _ctx = null!;
    private FileSystemWatcher? _watcher;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string _cmdDir = "";
    private string _cmdFile = "";

    // Client Credentials 令牌缓存
    private readonly object _tokenLock = new();
    private string? _token;
    private DateTime _tokenExpiry;

    public Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;

        _cmdDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SMTCPlayer", "plugins-data", "spotify-support");
        _cmdFile = Path.Combine(_cmdDir, "play-request.json");
        Directory.CreateDirectory(_cmdDir);

        StartWatching();
        ProcessPendingCommand(); // 宿主启动前落下的命令也处理

        var hasCreds = HasCredentials();
        _ctx.Log.Info($"已加载（SMTC 识别 + 搜索播放；Web API 凭证: {(hasCreds ? "已配置" : "未配置，请在 settings.json 填写 client_id/client_secret")}）");
        return Task.CompletedTask;
    }

    public Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
    {
        switch (pluginEvent.Type)
        {
            case PluginEventType.SongChanged:
                if (IsSpotify(pluginEvent.After) && !string.IsNullOrEmpty(pluginEvent.After?.Title))
                    _ctx.Log.Info($"Spotify 正在播放: {pluginEvent.After!.Artist} - {pluginEvent.After.Title}");
                break;

            case PluginEventType.PlaybackStateChanged:
                if (IsSpotify(pluginEvent.After))
                    _ctx.Log.Info($"Spotify {(pluginEvent.After!.IsPlaying ? "开始播放" : "已暂停")}");
                break;
        }
        return Task.CompletedTask;
    }

    public Task OnUnloadingAsync(CancellationToken cancellationToken)
    {
        StopWatching();
        _http.Dispose();
        _ctx.Log.Info("已卸载");
        return Task.CompletedTask;
    }

    // ============== 搜索提供者（网页端搜索/播放经宿主路由到此处） ==============

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=30");
        req.Headers.Authorization = new("Bearer", token);

        using var resp = await _http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Spotify 搜索失败: HTTP {(int)resp.StatusCode} {Truncate(body, 200)}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var items = new List<SearchResultItem>();
        if (!doc.RootElement.TryGetProperty("tracks", out var tracksEl)
            || !tracksEl.TryGetProperty("items", out var itemsEl)
            || itemsEl.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var t in itemsEl.EnumerateArray())
        {
            if (!t.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;

            string artists = "";
            if (t.TryGetProperty("artists", out var arEl) && arEl.ValueKind == JsonValueKind.Array)
                artists = string.Join(" / ", arEl.EnumerateArray()
                    .Where(a => a.TryGetProperty("name", out _))
                    .Select(a => a.GetProperty("name").GetString()));

            string album = "", cover = "";
            if (t.TryGetProperty("album", out var alEl))
            {
                if (alEl.TryGetProperty("name", out var alNameEl) && alNameEl.ValueKind == JsonValueKind.String)
                    album = alNameEl.GetString() ?? "";
                if (alEl.TryGetProperty("images", out var imgsEl) && imgsEl.ValueKind == JsonValueKind.Array)
                {
                    var first = imgsEl.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object
                        && first.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                        cover = urlEl.GetString() ?? "";
                }
            }

            items.Add(new SearchResultItem
            {
                Id = idEl.GetString() ?? "",
                Name = GetStr(t, "name"),
                Artists = artists,
                Album = album,
                Cover = cover.Length > 0 ? cover : null,
                Duration = t.TryGetProperty("duration_ms", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                    ? dEl.GetDouble()
                    : 0,
            });
        }
        return items;
    }

    public async Task<bool> PlayAsync(SearchResultItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
            throw new InvalidOperationException("缺少 Spotify 曲目 ID");

        var ok = await PlayUriAsync($"spotify:track:{item.Id}");
        if (!ok) _ctx.Log.Warn("无法找到 Spotify.exe，请确认已安装");
        return ok;
    }

    // ============== Web API（Client Credentials） ==============

    private bool HasCredentials()
    {
        _ctx.Settings.Reload(); // 允许用户改完 settings.json 无需重启
        return !string.IsNullOrWhiteSpace(_ctx.Settings.Get("client_id", ""))
            && !string.IsNullOrWhiteSpace(_ctx.Settings.Get("client_secret", ""));
    }

    /// <summary>获取访问令牌（缓存至过期前 60 秒）。无凭证或获取失败抛出带说明的异常。</summary>
    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        lock (_tokenLock)
        {
            if (_token != null && DateTime.UtcNow < _tokenExpiry) return _token;
        }

        _ctx.Settings.Reload();
        var clientId = _ctx.Settings.Get("client_id", "") ?? "";
        var clientSecret = _ctx.Settings.Get("client_secret", "") ?? "";
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("未配置 Spotify Web API 凭证，请在插件 settings.json 中填写 client_id 与 client_secret");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        req.Headers.Authorization = new("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

        using var resp = await _http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify 凭证无效或获取令牌失败: HTTP {(int)resp.StatusCode} {Truncate(body, 200)}");

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("令牌响应缺少 access_token");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var eEl) && eEl.ValueKind == JsonValueKind.Number
            ? eEl.GetInt32() : 3600;

        lock (_tokenLock)
        {
            _token = token;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
        }
        return token;
    }

    // ============== 命令文件 ==============

    private void StartWatching()
    {
        StopWatching();
        _watcher = new FileSystemWatcher(_cmdDir)
        {
            Filter = "*.json",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, _) => ProcessPendingCommand();
        _watcher.Changed += (_, _) => ProcessPendingCommand();
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>读取并执行命令文件（写入方原子替换；处理成功后删除）。</summary>
    private void ProcessPendingCommand()
    {
        try
        {
            if (!File.Exists(_cmdFile)) return;

            CommandRequest? cmd;
            try
            {
                cmd = JsonSerializer.Deserialize<CommandRequest>(File.ReadAllText(_cmdFile));
            }
            catch (JsonException)
            {
                Thread.Sleep(200); // 写入未完成，重读一次
                try { cmd = JsonSerializer.Deserialize<CommandRequest>(File.ReadAllText(_cmdFile)); }
                catch { _ctx.Log.Warn("命令文件 JSON 无效，已忽略"); File.Delete(_cmdFile); return; }
            }

            if (cmd == null || (string.IsNullOrWhiteSpace(cmd.Query) && string.IsNullOrWhiteSpace(cmd.Uri)))
            {
                _ctx.Log.Warn("命令文件缺少 query/uri，已删除");
                File.Delete(_cmdFile);
                return;
            }

            // 异步执行，避免阻塞调用方（FSW 回调 / 事件分发）
            _ = RunCommandAsync(cmd);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"处理命令文件异常: {ex.Message}");
        }
    }

    private async Task RunCommandAsync(CommandRequest cmd)
    {
        try
        {
            var uri = cmd.Uri;
            if (string.IsNullOrWhiteSpace(uri) && !string.IsNullOrWhiteSpace(cmd.Query))
            {
                _ctx.Log.Info($"搜索: {cmd.Query}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var results = await SearchAsync(cmd.Query, cts.Token);
                if (results.Count > 0)
                {
                    uri = $"spotify:track:{results[0].Id}";
                }
                else
                {
                    _ctx.Log.Warn($"未搜索到曲目，改为打开 Spotify 搜索页: {cmd.Query}");
                    uri = $"spotify:search:{cmd.Query}"; // 兜底：打开客户端搜索结果（不自动播放）
                }
            }

            if (await PlayUriAsync(uri!))
            {
                _ctx.Log.Info($"已发送播放: {uri}");
                TryDeleteCommand();
            }
            else
            {
                _ctx.Log.Error($"Spotify 启动失败，命令文件保留以便重试: {uri}");
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("执行播放命令失败", ex);
        }
    }

    private void TryDeleteCommand()
    {
        try { if (File.Exists(_cmdFile)) File.Delete(_cmdFile); } catch { }
    }

    // ============== 播放（URI 启动 Spotify 客户端） ==============

    private static async Task<bool> PlayUriAsync(string uri)
    {
        var exe = FindSpotifyExe();
        if (exe == null)
        {
            return false;
        }

        try
        {
            // 已在运行时 --uri 会在现有实例中播放；未运行则启动并播放
            using var proc = Process.Start(new ProcessStartInfo(exe, $"--uri={uri}") { UseShellExecute = false });
            await Task.Delay(500);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindSpotifyExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotify", "Spotify.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Spotify", "Spotify.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Spotify", "Spotify.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsSpotify(MediaSnapshot? snapshot) =>
        snapshot?.Source?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetStr(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class CommandRequest
    {
        public string? Query { get; set; }
        public string? Uri { get; set; }
    }
}

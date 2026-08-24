using System.Reflection;
using System.Text.Json;

namespace SMTCPlayer.Core.Services;

/// <summary>发现新版本时的信息。</summary>
public sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? ReleaseName);

public enum UpdateCheckStatus
{
    /// <summary>有新版本。</summary>
    UpdateAvailable,

    /// <summary>已是最新（或远端尚无发布版）。</summary>
    UpToDate,

    /// <summary>检查失败（网络等原因）。</summary>
    Failed,
}

/// <summary>检查结果：状态 + 可读消息 + 新版本信息（仅 Status=UpdateAvailable 时非空）。</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, UpdateInfo? Update);

/// <summary>
/// 应用更新检查器：查询 GitHub Releases 的最新发布并与当前程序集版本比较。
/// 仅做检查与提示，不下载、不自动更新；用户按提示自行前往发布页手动获取。
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>项目发布页（检查到新版本时引导用户前往）。</summary>
    public const string ReleasesPageUrl = "https://github.com/nczxe/SMTCPlayer/releases";

    private const string LatestPageUrl = "https://github.com/nczxe/SMTCPlayer/releases/latest";

    /// <summary>探测结果的内存缓存有效期：避免设置页连续点击反复联网。</summary>
    private static readonly TimeSpan ProbeCacheTtl = TimeSpan.FromMinutes(5);

    // 匿名 REST API 配额仅 60 次/小时/IP，故仅作重定向探测失败后的回退
    private const string LatestApiUrl = "https://api.github.com/repos/nczxe/SMTCPlayer/releases/latest";

    /// <summary>启动静默检查的最小间隔：24 小时内不重复联网检查。</summary>
    private static readonly TimeSpan StartupCheckInterval = TimeSpan.FromHours(24);

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMTCPlayer", "update.json");

    /// <summary>重定向探测客户端：不跟随 302，读 Location 即得最新 tag（不计 API 配额）。</summary>
    private static readonly Lazy<HttpClient> _probe = new(() =>
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SMTCPlayer/{CurrentVersion}");
        return client;
    });

    private static readonly Lazy<HttpClient> _api = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API 强制要求 User-Agent，否则返回 403
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SMTCPlayer/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    });

    private static (DateTime At, string Tag, string Name, string Url)? _cache;

    /// <summary>当前应用版本（取入口 exe 程序集，失败回落 Core 自身）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version
                    ?? Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// 手动检查更新（设置页按钮）：总是发起网络请求。
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct = default)
    {
        try
        {
            var release = await FetchLatestReleaseAsync(ct);
            if (release == null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, "仓库暂无已发布的版本。", null);
            }

            var latest = NormalizeVersion(release.Value.Tag);
            if (!Version.TryParse(latest, out var latestVer))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, $"无法解析远端版本号: {release.Value.Tag}", null);
            }

            if (!Version.TryParse(CurrentVersion, out var currentVer))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, $"无法解析当前版本号: {CurrentVersion}", null);
            }

            if (latestVer > currentVer)
            {
                var info = new UpdateInfo(CurrentVersion, latest, release.Value.Url, release.Value.Name);
                WriteState(DateTime.UtcNow, info.LatestVersion); // 静默提醒同步去重
                return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable,
                    $"发现新版本 v{info.LatestVersion}（当前 v{CurrentVersion}）。", info);
            }

            return new UpdateCheckResult(UpdateCheckStatus.UpToDate,
                $"已是最新版本 v{CurrentVersion}。", null);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, "检查超时。", null);
        }
        catch (Exception ex)
        {
            Logger.Warn($"检查更新失败: {ex.Message}");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, $"检查失败: {ex.Message}", null);
        }
    }

    /// <summary>
    /// 启动时静默检查：距上次检查不足 24 小时则直接跳过；
    /// 发现新版本且该版本尚未提醒过时返回 UpdateInfo 并记录"已提醒"，避免每次启动重复打扰。
    /// </summary>
    public async Task<UpdateInfo?> TryGetPendingStartupUpdateAsync()
    {
        try
        {
            var (lastCheck, notifiedTag) = ReadState();

            // 节流窗口内不联网
            if (lastCheck != null && DateTime.UtcNow - lastCheck < StartupCheckInterval)
            {
                return null;
            }

            var release = await FetchLatestReleaseAsync(CancellationToken.None);
            if (release == null)
            {
                WriteState(DateTime.UtcNow, null);
                return null;
            }

            // 刷新检查时间（保留已有提醒记录）
            WriteState(DateTime.UtcNow, null);

            var latest = NormalizeVersion(release.Value.Tag);
            if (!Version.TryParse(latest, out var latestVer) ||
                !Version.TryParse(CurrentVersion, out var currentVer) ||
                latestVer <= currentVer)
            {
                return null;
            }

            if (latest == notifiedTag)
            {
                return null; // 该版本已提醒过，不再重复打扰
            }

            var info = new UpdateInfo(CurrentVersion, latest, release.Value.Url, release.Value.Name);
            Logger.Info($"发现新版本: v{latest}（当前 v{CurrentVersion}）→ {info.ReleaseUrl}");

            // 记录已提醒，下次启动不再弹
            WriteState(DateTime.UtcNow, latest);
            return info;
        }
        catch (Exception ex)
        {
            Logger.Warn($"启动检查更新失败（忽略）: {ex.Message}");
            return null;
        }
    }

    // ============== 内部实现 ==============

    /// <summary>
    /// 获取最新发布信息。优先走 releases/latest 页面的 302 重定向探测（无 API 配额），
    /// 失败时回退到匿名 REST API（60 次/小时/IP）。
    /// 返回 null 表示仓库暂无正式发布。
    /// </summary>
    private async Task<(string Tag, string Name, string Url)?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        var cached = _cache;
        if (cached != null && DateTime.UtcNow - cached.Value.At < ProbeCacheTtl)
        {
            return (cached.Value.Tag, cached.Value.Name, cached.Value.Url);
        }

        // 1) 重定向探测（主路径）
        try
        {
            var tag = await ProbeTagViaRedirectAsync(ct);
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _cache = (DateTime.UtcNow, tag!, tag!, TagUrl(tag!));
                return (tag!, tag!, TagUrl(tag!));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Debug($"重定向探测失败，回退 REST API: {ex.Message}");
        }

        // 2) REST API（回退，受匿名配额限制）
        return await FetchViaRestApiAsync(ct);
    }

    /// <summary>访问 /releases/latest，从 302 Location 提取最新发布 tag。非重定向响应返回 null。</summary>
    private static async Task<string?> ProbeTagViaRedirectAsync(CancellationToken ct)
    {
        using var resp = await _probe.Value.GetAsync(LatestPageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        var location = resp.Headers.Location;
        if ((int)resp.StatusCode is < 300 or >= 400 || location == null)
        {
            return null; // 无发布版（404）或策略变化 → 交给回退路径判断
        }

        var abs = location.IsAbsoluteUri ? location : new Uri(new Uri(LatestPageUrl), location);
        var path = abs.AbsolutePath;
        var marker = "/releases/tag/";
        var idx = path.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var tag = Uri.UnescapeDataString(path[(idx + marker.Length)..].TrimEnd('/'));
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private async Task<(string Tag, string Name, string Url)?> FetchViaRestApiAsync(CancellationToken ct)
    {
        using var resp = await _api.Value.GetAsync(LatestApiUrl, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden &&
            resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remain) &&
            remain.FirstOrDefault() == "0")
        {
            throw new InvalidOperationException("GitHub 匿名额度已用尽（60 次/小时/IP），请约 1 小时后重试");
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // 仓库还没有任何正式发布
        }
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new InvalidOperationException("响应缺少 tag_name 字段");
        }

        return (tag, name ?? tag, string.IsNullOrWhiteSpace(url) ? ReleasesPageUrl : url!);
    }

    private static string TagUrl(string tag) => $"{ReleasesPageUrl}/tag/{Uri.EscapeDataString(tag)}";

    /// <summary>规范化版本标签："v1.2.0"、"1.2.0-beta.1"、"V1.2.0+build" → "1.2.0"。</summary>
    internal static string NormalizeVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        return s.Trim();
    }

    // ============== 状态持久化（%LocalAppData%\SMTCPlayer\update.json） ==============

    private (DateTime? LastCheckUtc, string? NotifiedTag) ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
            var root = doc.RootElement;
            DateTime? last = null;
            if (root.TryGetProperty("lastCheckUtc", out var t) &&
                DateTime.TryParse(t.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                last = dt.ToUniversalTime();
            }
            var tag = root.TryGetProperty("notifiedTag", out var n) ? n.GetString() : null;
            return (last, tag);
        }
        catch
        {
            return (null, null);
        }
    }

    private void WriteState(DateTime lastCheckUtc, string? notifiedTag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);

            string json;
            if (notifiedTag == null)
            {
                // 只刷新检查时间，保留已有提醒记录
                var (_, keepTag) = ReadState();
                json = JsonSerializer.Serialize(new StateDto
                {
                    LastCheckUtc = lastCheckUtc,
                    NotifiedTag = keepTag,
                });
            }
            else
            {
                json = JsonSerializer.Serialize(new StateDto
                {
                    LastCheckUtc = lastCheckUtc,
                    NotifiedTag = notifiedTag,
                });
            }

            File.WriteAllText(StateFile, json);
        }
        catch
        {
            // 持久化失败不影响功能，仅可能重复提醒一次
        }
    }

    private sealed class StateDto
    {
        public DateTime LastCheckUtc { get; set; }
        public string? NotifiedTag { get; set; }
    }
}

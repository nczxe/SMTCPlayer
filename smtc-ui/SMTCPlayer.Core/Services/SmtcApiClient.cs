using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SMTCPlayer.Core.Models;
using SMTCPlayer.Core.Plugins;

namespace SMTCPlayer.Core.Services;

public class SmtcApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _host;
    private string? _authToken;

    public string BaseUrl { get; private set; }
    public bool HasToken => _authToken != null;

    /// <summary>
    /// 宿主聚合的插件能力（逗号分隔，如 "search,playlists"；无能力时为 "none"）。
    /// 随每次请求上报，后端据此向网页端暴露功能开关。空串表示不发送。
    /// </summary>
    public string? CapabilitiesHeader { private get; set; }

    public SmtcApiClient(string host, int port) : this(host, port, null)
    {
    }

    public SmtcApiClient(string host, int port, HttpClient? httpClient)
    {
        _host = host;
        BaseUrl = $"http://{host}:{port}";
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<AuthStatus?> GetAuthStatusAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<AuthStatus>($"{BaseUrl}/api/auth/status");
        }
        catch { return null; }
    }

    public async Task<AuthResponse?> SetupPinAsync(string pin)
    {
        var result = await PostJsonAsync<AuthResponse>("/api/auth/setup", new { pin });
        if (result?.Success == true && result.Token != null)
        {
            _authToken = result.Token;
        }
        return result;
    }

    public async Task<AuthResponse?> LoginAsync(string pin)
    {
        var result = await PostJsonAsync<AuthResponse>("/api/auth/login", new { pin });
        if (result?.Success == true && result.Token != null)
        {
            _authToken = result.Token;
        }
        return result;
    }

    public async Task<AuthResponse?> ChangePinAsync(string oldPin, string newPin)
    {
        return await PostJsonAsync<AuthResponse>("/api/auth/change_pin", new { old_pin = oldPin, new_pin = newPin });
    }

    public async Task<AuthResponse?> ResetPinAsync(string newPin)
    {
        return await PostJsonAsync<AuthResponse>("/api/auth/reset_pin", new { new_pin = newPin });
    }

    public void ClearToken()
    {
        _authToken = null;
    }

    public void SetPort(int port)
    {
        BaseUrl = $"http://{_host}:{port}";
    }

    public async Task<PlayerStatus?> GetStatusAsync()
    {
        try
        {
            return await GetJsonAsync<PlayerStatus>("/api/status");
        }
        catch { return null; }
    }

    public async Task<bool> SendControlAsync(string action)
    {
        var result = await PostJsonAsync<ApiResponse>($"/api/{action}", null);
        return result?.Success == true;
    }

    public async Task<bool> SetVolumeAsync(double volume)
    {
        var result = await PostJsonAsync<ApiResponse>("/api/volume", new { volume });
        return result?.Success == true;
    }

    public async Task<bool> ToggleMuteAsync()
    {
        var result = await PostJsonAsync<ApiResponse>("/api/volume/toggle_mute", null);
        return result?.Success == true;
    }

    public async Task<HealthStatus?> GetHealthAsync()
    {
        try
        {
            return await GetJsonAsync<HealthStatus>("/api/health");
        }
        catch { return null; }
    }

    // ============== 插件任务桥（网页端 ↔ 宿主 ↔ 插件） ==============

    /// <summary>取出待处理的插件调用任务（Flask 任务队列，宿主轮询）。</summary>
    public async Task<List<PluginJob>?> GetPendingPluginJobsAsync()
    {
        try
        {
            return await GetJsonAsync<List<PluginJob>>("/api/plugin/jobs/next");
        }
        catch { return null; }
    }

    /// <summary>回传任务执行结果给 Flask（唤醒等待中的网页端长轮询）。</summary>
    public async Task<bool> PostPluginJobResultAsync(string jobId, object result)
    {
        try
        {
            var resp = await PostJsonAsync<ApiResponse>($"/api/plugin/jobs/{jobId}/result", result);
            return resp?.Success == true;
        }
        catch { return false; }
    }

    /// <summary>上报当前可用的搜索提供者列表（提供者集合变化时调用）。</summary>
    public async Task<bool> ReportPluginProvidersAsync(object providers)
    {
        try
        {
            var resp = await PostJsonAsync<ApiResponse>("/api/plugin/providers", providers);
            return resp?.Success == true;
        }
        catch { return false; }
    }

    private void ApplyCommonHeaders(HttpRequestMessage req)
    {
        if (_authToken != null)
            req.Headers.Add("X-SMTC-Token", _authToken);
        if (!string.IsNullOrEmpty(CapabilitiesHeader))
            req.Headers.Add("X-SMTC-Capabilities", CapabilitiesHeader);
    }

    private async Task<T?> GetJsonAsync<T>(string path) where T : class
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        ApplyCommonHeaders(req);
        var resp = await _http.SendAsync(req);
        return await resp.Content.ReadFromJsonAsync<T>();
    }

    private async Task<T?> PostJsonAsync<T>(string path, object? body) where T : class
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}");
        ApplyCommonHeaders(req);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        return await resp.Content.ReadFromJsonAsync<T>();
    }

    public void Dispose() => _http.Dispose();
}

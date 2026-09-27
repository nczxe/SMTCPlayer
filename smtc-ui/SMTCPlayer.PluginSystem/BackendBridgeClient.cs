using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMTCPlayer.PluginSystem;

/// <summary>
/// 后端桥接客户端：插件宿主访问本地后端服务（Flask）所需的端点子集，
/// 语义与 SmtcApiClient 逐一对齐（URL、HTTP 方法、默认 JSON 序列化、回环免认证——不携带 token）。
/// 单一 HttpClient 实例复用（5 秒超时，与 SmtcApiClient 一致）。
/// 全部方法宽松失败：异常时返回 false / null，不向调用方抛出。
/// </summary>
internal sealed class BackendBridgeClient
{
    private readonly HttpClient _http;

    /// <summary>后端基础地址（如 http://127.0.0.1:8888）。</summary>
    public string BaseUrl { get; }

    public BackendBridgeClient(string baseUrl)
    {
        BaseUrl = baseUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>播放控制（play / pause / play_pause / next / previous）。</summary>
    public async Task<bool> SendControlAsync(string action)
    {
        try
        {
            var result = await PostJsonAsync<ApiResponse>($"/api/{action}", null);
            return result?.Success == true;
        }
        catch { return false; }
    }

    public async Task<bool> ToggleMuteAsync()
    {
        try
        {
            var result = await PostJsonAsync<ApiResponse>("/api/volume/toggle_mute", null);
            return result?.Success == true;
        }
        catch { return false; }
    }

    public async Task<bool> SetVolumeAsync(int volume)
    {
        try
        {
            var result = await PostJsonAsync<ApiResponse>("/api/volume", new { volume });
            return result?.Success == true;
        }
        catch { return false; }
    }

    /// <summary>取出待处理的插件调用任务（后端任务队列，宿主轮询）。</summary>
    public async Task<List<PluginJob>?> GetPendingPluginJobsAsync()
    {
        try
        {
            return await GetJsonAsync<List<PluginJob>>("/api/plugin/jobs/next");
        }
        catch { return null; }
    }

    /// <summary>回传任务执行结果（唤醒等待中的网页端长轮询）。</summary>
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
    public async Task<bool> ReportPluginProvidersAsync(object payload)
    {
        try
        {
            var resp = await PostJsonAsync<ApiResponse>("/api/plugin/providers", payload);
            return resp?.Success == true;
        }
        catch { return false; }
    }

    private async Task<T?> GetJsonAsync<T>(string path) where T : class
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        var resp = await _http.SendAsync(req);
        return await resp.Content.ReadFromJsonAsync<T>();
    }

    private async Task<T?> PostJsonAsync<T>(string path, object? body) where T : class
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}");
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        return await resp.Content.ReadFromJsonAsync<T>();
    }

    /// <summary>后端通用响应（success/error），JSON 结构与后端 API 契约一致。</summary>
    private sealed class ApiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}

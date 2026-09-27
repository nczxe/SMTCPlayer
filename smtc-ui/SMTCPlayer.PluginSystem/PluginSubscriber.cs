using SMTCPlayer.PluginApi;

namespace SMTCPlayer.PluginSystem;

/// <summary>
/// 插件订阅者适配器：把广播池的事件路由给单个插件实例。
/// 分发前检查启用状态与实例存活；在途计数（InFlight）供卸载序列
/// 等待事件处理归零，配合插件专属取消令牌实现安全热卸载。
/// </summary>
internal sealed class PluginSubscriber : IBroadcastSubscriber
{
    private readonly PluginManager.PluginRecord _record;

    public PluginSubscriber(PluginManager.PluginRecord record) => _record = record;

    /// <summary>订阅者标识（即插件 Id，仅用于日志）。</summary>
    public string? SubscriberId => _record.Info.Id;

    public async Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
    {
        var instance = _record.Instance;
        if (!_record.Info.IsEnabled || instance == null) return;

        Interlocked.Increment(ref _record.InFlight);
        try
        {
            // 用插件专属令牌而非池的令牌：卸载时可单独取消本插件在途的事件处理
            await instance.HandleEventAsync(pluginEvent, _record.OwnCts!.Token);
        }
        finally
        {
            Interlocked.Decrement(ref _record.InFlight);
        }
    }
}

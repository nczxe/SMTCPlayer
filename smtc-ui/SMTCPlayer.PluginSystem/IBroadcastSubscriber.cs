using SMTCPlayer.PluginApi;

namespace SMTCPlayer.PluginSystem;

/// <summary>
/// 广播池订阅者：按注册顺序接收事件，接收顺序与发布顺序一致。
/// 实现应尽快返回（异步等待而非同步阻塞），以免拖慢分发循环导致事件被丢弃。
/// 单订阅者抛出的异常会被广播池隔离，不影响其他订阅者与分发循环。
/// </summary>
public interface IBroadcastSubscriber
{
    /// <summary>订阅者标识（仅用于日志，可为 null）。</summary>
    string? SubscriberId { get; }

    /// <summary>处理一条广播事件。</summary>
    Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken);
}

using SMTCPlayer.PluginApi;
using SMTCPlayer.PluginSystem;

namespace SMTCPlayer.Core.LanProtocol;

/// <summary>
/// 广播池订阅适配器：把插件事件流转交给 LAN 服务，由其向已鉴权连接推送 status。
/// 处理逻辑全部委托给服务，自身不阻塞分发循环之外的工作。
/// </summary>
internal sealed class LanBroadcastSubscriber : IBroadcastSubscriber
{
    private readonly LanProtocolServer _server;

    public LanBroadcastSubscriber(LanProtocolServer server) => _server = server;

    public string? SubscriberId => "LanProtocol";

    public Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
        => _server.OnBroadcastAsync(pluginEvent, cancellationToken);
}

namespace SMTCPlayer.PluginApi;

/// <summary>
/// SMTC Player 插件契约。
/// 插件被动接收 Core 广播的事件（SMTC 变化 → Core → 广播 → 所有插件），
/// 不持有宿主内部对象；初始化时通过 <see cref="IPluginContext"/> 获得受控访问面。
/// </summary>
public interface IPlugin
{
    /// <summary>插件加载完成后调用一次，传入受控上下文。</summary>
    Task OnLoadedAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>应用退出前调用，插件应在此保存状态（Settings.Save 等）。</summary>
    Task OnUnloadingAsync(CancellationToken cancellationToken);

    /// <summary>收到 Core 广播的事件。所有事件都会送达，插件自行按 <see cref="PluginEvent.Type"/> 过滤。</summary>
    Task HandleEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken);
}

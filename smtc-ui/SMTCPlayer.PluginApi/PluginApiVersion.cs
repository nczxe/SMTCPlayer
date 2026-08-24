namespace SMTCPlayer.PluginApi;

/// <summary>
/// 插件契约（PluginApi）的 ABI 版本。
/// 仅在发生破坏性变更（删改接口成员、改语义）时递增；
/// 纯新增成员不递增。插件在 plugin.json 的 apiVersion 中声明所需版本，
/// 宿主加载时校验，高于宿主实现则拒绝加载并给出可读错误。
/// </summary>
public static class PluginApiVersion
{
    public const int Current = 1;
}

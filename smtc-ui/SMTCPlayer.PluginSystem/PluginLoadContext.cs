using System.Reflection;
using System.Runtime.Loader;

namespace SMTCPlayer.PluginSystem;

/// <summary>
/// 插件隔离加载上下文：优先从插件目录解析依赖；
/// 契约程序集（SMTCPlayer.PluginApi）回落到默认上下文，与宿主共享同一份 IPlugin 类型；
/// 宿主程序集黑名单：插件引用宿主（Core / PluginSystem / WinUI / Wpf）时抛出
/// 带可读文案的 <see cref="PluginLoadException"/> 拒绝加载，而不是静默回落默认上下文
/// 导致类型身份混用与卸载失效。
/// 可收集（collectible）：禁用/热更新时调用 <see cref="AssemblyLoadContext.Unload"/>，
/// 在宿主与插件均不残留强引用的前提下程序集可被真正卸载。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>宿主程序集黑名单：插件不允许引用，引用即抛异常拒绝加载。</summary>
    private static readonly HashSet<string> HostAssemblyBlacklist = new(StringComparer.Ordinal)
    {
        "SMTCPlayer.Core",
        "SMTCPlayer.PluginSystem",
        "SMTCPlayer.WinUI",
        "SMTCPlayer.Wpf",
    };

    private readonly string _dir;

    public PluginLoadContext(string pluginId, string dir)
        : base($"plugin-{pluginId}", isCollectible: true)
    {
        _dir = dir;
    }

    /// <summary>判断给定程序集名是否属于禁止插件引用的宿主程序集（供加载期预扫描复用）。</summary>
    public static bool IsForbiddenHostAssembly(string? assemblyName)
        => assemblyName != null && HostAssemblyBlacklist.Contains(assemblyName);

    protected override Assembly? Load(AssemblyName name)
    {
        // 契约程序集必须与宿主共享同一份（单一 Assembly 身份），否则 IPlugin 类型不匹配
        if (name.Name == "SMTCPlayer.PluginApi") return null;

        // 宿主程序集：插件不允许引用，抛异常阻止（可读文案经 GetBaseException 透出到 PluginInfo.Error）
        if (IsForbiddenHostAssembly(name.Name))
            throw new PluginLoadException(
                $"插件不允许引用宿主程序集 {name.Name}（插件只能依赖 SMTCPlayer.PluginApi 与自带依赖）");

        var path = Path.Combine(_dir, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null; // null → 回落默认上下文（运行时库）
    }
}

/// <summary>
/// 插件加载被宿主主动拒绝时抛出（例如引用了禁止的宿主程序集）。
/// 与一般 IO 失败（如 FileNotFoundException）语义不同：这是插件系统对隔离边界的
/// "主动保护"，而非运行到某方法时才发生的"碰巧加载不到"。
/// </summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message) : base(message) { }

    public PluginLoadException(string message, Exception innerException) : base(message, innerException) { }
}

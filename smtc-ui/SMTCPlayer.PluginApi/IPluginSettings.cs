namespace SMTCPlayer.PluginApi;

/// <summary>插件隔离的键值设置存储（每个插件独立持久化，互不可见）。</summary>
public interface IPluginSettings
{
    /// <summary>读取键值；不存在或反序列化失败时返回 <paramref name="fallback"/>。</summary>
    T? Get<T>(string key, T? fallback = default);

    /// <summary>写入键值（仅内存，需调用 <see cref="Save"/> 持久化）。</summary>
    void Set<T>(string key, T value);

    /// <summary>立即持久化到磁盘。</summary>
    void Save();

    /// <summary>丢弃未保存的修改并重新从磁盘加载。</summary>
    void Reload();
}

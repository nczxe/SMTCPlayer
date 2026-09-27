namespace SMTCPlayer.Core.LanProtocol;

/// <summary>局域网服务监听范围。</summary>
public enum LanListenScope
{
    /// <summary>仅回环（127.0.0.1），仅本机应用可连。</summary>
    Loopback = 0,

    /// <summary>全网卡（0.0.0.0），局域网内其他设备可连。</summary>
    AllInterfaces = 1,
}

/// <summary>
/// 局域网协议服务配置。所有限制项均在此集中定义，便于按需收紧。
/// </summary>
public sealed class LanProtocolOptions
{
    /// <summary>默认监听端口。</summary>
    public const int DefaultPort = 9000;

    /// <summary>是否启用服务（由设置页开关控制）。</summary>
    public bool Enabled { get; set; }

    /// <summary>监听端口，默认 9000。</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>监听范围，默认仅回环。</summary>
    public LanListenScope Scope { get; set; } = LanListenScope.Loopback;

    /// <summary>单条消息（一行 NDJSON）字节上限，默认 64KB；超出即断开该连接。</summary>
    public int MaxMessageBytes { get; set; } = 64 * 1024;

    /// <summary>最大并发已鉴权连接数，默认 16；超出拒绝新连接。</summary>
    public int MaxAuthenticatedConnections { get; set; } = 16;

    /// <summary>单连接最多鉴权尝试次数，默认 5；超限断开。</summary>
    public int MaxAuthAttempts { get; set; } = 5;

    /// <summary>空闲超时：超过该时长未收到任何消息即断开。</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>心跳间隔：向已鉴权连接发送 ping 的周期。</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>复制一份配置（服务内部持有副本，避免外部后续修改影响运行中服务）。</summary>
    public LanProtocolOptions Clone() => (LanProtocolOptions)MemberwiseClone();
}

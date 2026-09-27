using System.Diagnostics;
using System.Text;

namespace SMTCPlayer.Logging;

/// <summary>
/// 后台写线程专用的文件写入器：主日志按日命名并按大小分卷、
/// 插件日志路由到 plugins 子目录（Warn/Error 镜像进主日志）、
/// 序号缺口法拥塞统计与过期清理。实例状态由写线程与清理路径共享，内部以锁保护。
/// </summary>
internal sealed class InternalLogWriter
{
    private const int RetentionDays = 7;
    private const long TotalBudgetBytes = 50L * 1024 * 1024;
    private const string PluginPrefix = "Plugin:";

    private readonly string _dir;
    private readonly string _pluginsDir;
    private readonly object _ioLock = new();
    private readonly Dictionary<string, PluginFileState> _pluginFiles = new();

    private string _mainBaseName = "";
    private int _mainPartIndex = 1;
    private string _mainLogPath = "";
    private DateTime _mainFileDate;

    private long _lastSeq;
    private long _droppedTotal;
    private long _droppedReported;
    private long _lastCleanupTick = Environment.TickCount64;

    private sealed class PluginFileState
    {
        public string Path = "";
        public int PartIndex = 1;
    }

    public InternalLogWriter(string dir)
    {
        _dir = dir;
        _pluginsDir = Path.Combine(dir, "plugins");
        Directory.CreateDirectory(dir);
        _mainFileDate = DateTime.Now;
        _mainBaseName = $"smtc-{_mainFileDate:yyyyMMdd}";
        _mainPartIndex = FindWritablePart(_dir, _mainBaseName, LogManager.MainFileMaxBytes, 1);
        _mainLogPath = MainPartPath(_mainBaseName, _mainPartIndex);
    }

    /// <summary>当前主日志文件完整路径。</summary>
    public string MainLogPath
    {
        get { lock (_ioLock) return _mainLogPath; }
    }

    /// <summary>
    /// 批量写盘：先按发布序号排序（消除并发入队的微小乱序，保证缺口统计准确），
    /// 插件条目路由到 plugins/&lt;id&gt;.log，其中 Warn/Error 同时镜像进主日志。
    /// </summary>
    public void WriteBatch(List<LogEntry> batch)
    {
        lock (_ioLock)
        {
            try
            {
                if (batch.Count > 1)
                    batch.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));

                RollDateIfNeeded();

                var mainSb = new StringBuilder();
                Dictionary<string, StringBuilder>? pluginSbs = null;

                foreach (var entry in batch)
                {
                    TrackDrops(entry);

                    if (LogManager.IsPluginCategory(entry.Category))
                    {
                        var id = SanitizePluginId(entry.Category[PluginPrefix.Length..]);
                        pluginSbs ??= new Dictionary<string, StringBuilder>();
                        if (!pluginSbs.TryGetValue(id, out var sb))
                        {
                            sb = new StringBuilder();
                            pluginSbs[id] = sb;
                        }
                        sb.AppendLine(FormatLine(entry));
                        // 插件告警镜像进主日志（行内 category 已带 Plugin: 前缀）
                        if (entry.Level >= LogLevel.Warn)
                            mainSb.AppendLine(FormatLine(entry));
                    }
                    else
                    {
                        mainSb.AppendLine(FormatLine(entry));
                    }
                }

                if (mainSb.Length > 0)
                    AppendMain(mainSb.ToString());

                if (pluginSbs != null)
                    foreach (var pair in pluginSbs)
                        if (pair.Value.Length > 0)
                            AppendPlugin(pair.Key, pair.Value.ToString());
            }
            catch { /* 任何 IO 失败都不能中断写线程 */ }
        }
    }

    /// <summary>
    /// 把一条文本直接写入主日志（不经发布管线）。
    /// 用于拥塞丢弃告警等自指型汇总，避免其入队引发递归。
    /// </summary>
    public void WriteDirectLine(LogLevel level, string category, string message)
    {
        lock (_ioLock)
        {
            try
            {
                var line = LogManager.FormatLine(DateTime.Now, level, category, message);
                File.AppendAllText(_mainLogPath, line + Environment.NewLine, Encoding.UTF8);
                Debug.WriteLine(line);
            }
            catch { }
        }
    }

    /// <summary>每小时调用一次：清理过期（7 天）与超量（50MB）日志。</summary>
    public void PeriodicCleanup(long nowTick)
    {
        if (nowTick - _lastCleanupTick < 3_600_000) return;
        _lastCleanupTick = nowTick;
        CleanupExpired();
    }

    /// <summary>
    /// 删除超过保留期的 .log 文件，并保证整个 logs 目录（含 plugins 子目录）
    /// 总量不超过 50MB：超限时按最旧到新删除，正在写入的当前文件不删。
    /// </summary>
    public void CleanupExpired()
    {
        lock (_ioLock)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                var protectedPaths = BuildProtectedPaths();
                var files = new List<FileInfo>();
                if (Directory.Exists(_dir))
                {
                    foreach (var file in Directory.EnumerateFiles(_dir, "*.log", SearchOption.AllDirectories))
                    {
                        try { files.Add(new FileInfo(file)); } catch { }
                    }
                }

                foreach (var info in files)
                {
                    if (protectedPaths.Contains(info.FullName)) continue;
                    if (info.LastWriteTime < cutoff)
                    {
                        try { info.Delete(); } catch { }
                    }
                }

                var remaining = new List<FileInfo>();
                foreach (var info in files)
                {
                    if (protectedPaths.Contains(info.FullName)) continue;
                    try { if (info.Exists) remaining.Add(info); } catch { }
                }

                long total = 0;
                foreach (var info in remaining) total += info.Length;
                if (total <= TotalBudgetBytes) return;

                remaining.Sort(static (a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime));
                foreach (var info in remaining)
                {
                    if (total <= TotalBudgetBytes) break;
                    try
                    {
                        var length = info.Length;
                        info.Delete();
                        total -= length;
                    }
                    catch { }
                }
            }
            catch { /* 清理失败不影响写入 */ }
        }
    }

    // ===== 拥塞统计（序号缺口法） =====

    /// <summary>
    /// 发布序号出现缺口即视为队列拥塞丢弃（DropOldest）；
    /// 每累计新增 10 条丢弃，直接写一条 Warn 到主日志（不回管线，防递归）。
    /// </summary>
    private void TrackDrops(LogEntry entry)
    {
        if (entry.Sequence <= _lastSeq) return; // 排序后不应出现，防御
        if (entry.Sequence > _lastSeq + 1)
        {
            _droppedTotal += entry.Sequence - _lastSeq - 1;
            if (_droppedTotal - _droppedReported >= 10)
            {
                _droppedReported = _droppedTotal;
                WriteDirectLine(LogLevel.Warn, "Logging", $"日志队列拥塞，累计丢弃 {_droppedTotal} 条");
            }
        }
        _lastSeq = entry.Sequence;
    }

    // ===== 主日志写入与分卷 =====

    private void RollDateIfNeeded()
    {
        var now = DateTime.Now;
        if (now.Date == _mainFileDate.Date) return;
        _mainFileDate = now;
        _mainBaseName = $"smtc-{now:yyyyMMdd}";
        _mainPartIndex = FindWritablePart(_dir, _mainBaseName, LogManager.MainFileMaxBytes, 1);
        _mainLogPath = MainPartPath(_mainBaseName, _mainPartIndex);
    }

    private void AppendMain(string text)
    {
        File.AppendAllText(_mainLogPath, text, Encoding.UTF8);
        if (new FileInfo(_mainLogPath).Length >= LogManager.MainFileMaxBytes)
        {
            _mainPartIndex = FindWritablePart(_dir, _mainBaseName, LogManager.MainFileMaxBytes, _mainPartIndex + 1);
            _mainLogPath = MainPartPath(_mainBaseName, _mainPartIndex);
        }
    }

    private string MainPartPath(string baseName, int part) => part <= 1
        ? Path.Combine(_dir, baseName + ".log")
        : Path.Combine(_dir, $"{baseName}.part{part}.log");

    /// <summary>
    /// 在指定目录内从 minPart 起寻找第一个"不存在或未写满"的分卷号；
    /// 现有分卷全部写满时返回最大分卷号 + 1（开新卷）。
    /// </summary>
    private static int FindWritablePart(string dir, string baseName, long maxBytes, int minPart)
    {
        var maxPart = 0;
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, baseName + "*.log"))
            {
                var part = ParsePartIndex(Path.GetFileNameWithoutExtension(file), baseName);
                if (part > maxPart) maxPart = part;
            }
        }
        for (var part = Math.Max(1, minPart); part <= maxPart; part++)
        {
            var path = part <= 1
                ? Path.Combine(dir, baseName + ".log")
                : Path.Combine(dir, $"{baseName}.part{part}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < maxBytes)
                return part;
        }
        return maxPart + 1;
    }

    /// <summary>把 "smtc-20260926" / "smtc-20260926.part3" 解析为分卷号（主文件=1，不匹配=0）。</summary>
    private static int ParsePartIndex(string fileNameWithoutExtension, string baseName)
    {
        if (!fileNameWithoutExtension.StartsWith(baseName, StringComparison.Ordinal)) return 0;
        var suffix = fileNameWithoutExtension[baseName.Length..];
        if (suffix.Length == 0) return 1;
        if (suffix.StartsWith(".part", StringComparison.Ordinal) && int.TryParse(suffix[5..], out var part)) return part;
        return 0;
    }

    // ===== 插件日志写入与分卷 =====

    private void AppendPlugin(string id, string text)
    {
        if (!_pluginFiles.TryGetValue(id, out var state))
        {
            state = new PluginFileState();
            var path = Path.Combine(_pluginsDir, id + ".log");
            if (File.Exists(path) && new FileInfo(path).Length >= LogManager.PluginFileMaxBytes)
            {
                state.PartIndex = FindWritablePart(_pluginsDir, id, LogManager.PluginFileMaxBytes, 2);
                path = PluginPartPath(id, state.PartIndex);
            }
            state.Path = path;
            _pluginFiles[id] = state;
        }

        Directory.CreateDirectory(_pluginsDir); // 惰性创建，避免无插件时留下空目录
        File.AppendAllText(state.Path, text, Encoding.UTF8);
        if (new FileInfo(state.Path).Length >= LogManager.PluginFileMaxBytes)
        {
            state.PartIndex = FindWritablePart(_pluginsDir, id, LogManager.PluginFileMaxBytes, state.PartIndex + 1);
            state.Path = PluginPartPath(id, state.PartIndex);
        }
    }

    private string PluginPartPath(string id, int part) => part <= 1
        ? Path.Combine(_pluginsDir, id + ".log")
        : Path.Combine(_pluginsDir, $"{id}.part{part}.log");

    /// <summary>插件 id 转合法文件名（防御非法路径字符）。</summary>
    private static string SanitizePluginId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "unknown";
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return id;
    }

    // ===== 其他 =====

    private HashSet<string> BuildProtectedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { set.Add(Path.GetFullPath(_mainLogPath)); } catch { }
        foreach (var state in _pluginFiles.Values)
        {
            try { set.Add(Path.GetFullPath(state.Path)); } catch { }
        }
        return set;
    }

    private static string FormatLine(LogEntry entry) =>
        LogManager.FormatLine(entry.Timestamp, entry.Level, entry.Category, entry.Message);
}

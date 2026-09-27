using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using SMTCPlayer.Logging;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.PluginSystem;

/// <summary>插件安装/升级/卸载结果状态。</summary>
public enum PluginInstallStatus
{
    /// <summary>全新安装成功。</summary>
    Installed,
    /// <summary>升级成功（同 Id 旧版本存在且版本不同）。</summary>
    Upgraded,
    /// <summary>同版本覆盖安装成功（用户确认覆盖后）。</summary>
    Overwritten,
    /// <summary>安装包无效：无 plugin.json / 清单缺必填字段 / Id 非法 / 缺程序集 / 含路径穿越或绝对路径条目等。</summary>
    InvalidPackage,
    /// <summary>插件声明的 apiVersion / minHostVersion 超过当前应用所支持。</summary>
    Incompatible,
    /// <summary>Id 冲突：已安装相同版本但未确认覆盖，或 Id 被内置插件占用。</summary>
    IdConflict,
    /// <summary>安装或升级失败（详见 Error；RolledBack 表示是否已回滚旧版本）。</summary>
    Failed,
}

/// <summary>安装/升级结果：成功时 Plugin 为宿主内的最新描述；失败时 Error/RolledBack 携带详情。</summary>
public sealed class PluginInstallResult
{
    public required PluginInstallStatus Status { get; init; }

    /// <summary>操作完成后的插件描述（回滚成功时为恢复后的旧版本）。</summary>
    public PluginInfo? Plugin { get; init; }
    public string? OldVersion { get; init; }
    public string? NewVersion { get; init; }
    public string? Error { get; init; }

    /// <summary>失败后是否已把旧版本恢复原位并重新激活（全新安装失败无备份可回滚，恒为 false）。</summary>
    public bool RolledBack { get; init; }
}

/// <summary>卸载结果。</summary>
public sealed class PluginUninstallResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>安装选项（保守默认：同版本不覆盖、卸载不删数据）。</summary>
public sealed class PluginInstallOptions
{
    /// <summary>已安装相同版本时是否允许直接覆盖（默认 false：冲突时返回 IdConflict，由用户确认后重试）。</summary>
    public bool OverwriteSameVersion { get; set; }

    /// <summary>卸载时是否同时删除插件数据（默认 false：保留 plugins-data 与启用状态，便于重装恢复）。</summary>
    public bool RemoveDataOnUninstall { get; set; }
}

/// <summary>
/// 插件安装/升级/卸载器：把 zip 安装包安全落到用户插件目录，并驱动 PluginManager 完成热激活与失败回滚。
///
/// 目录布局（暂存/备份/回收与插件目录同卷，Directory.Move 原子生效）：
///   %LocalAppData%\SMTCPlayer\plugins\&lt;id&gt;\                          —— 正式安装位
///   %LocalAppData%\SMTCPlayer\plugin-maintenance\staging\&lt;guid&gt;\    —— 解压暂存（校验通过前不动正式目录）
///   %LocalAppData%\SMTCPlayer\plugin-maintenance\backup\&lt;id&gt;\&lt;ts&gt;\ —— 升级备份（每 Id 仅保留最近 1 份）
///   %LocalAppData%\SMTCPlayer\plugin-maintenance\trash\&lt;id&gt;\&lt;ts&gt;\  —— 卸载/坏版本回收
/// 维护目录刻意放在插件扫描根（plugins\）之外：PluginManager.RescanAsync 会枚举扫描根下全部子目录，
/// 含 plugin.json 的备份/回收目录若在扫描根内会被重新发现为插件（"僵尸插件"）。
/// </summary>
public sealed class PluginInstaller
{
    private static readonly ILog Log = LogManager.GetLogger("PluginSystem");

    private static readonly string MaintenanceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMTCPlayer", "plugin-maintenance");
    private static readonly string StagingRoot = Path.Combine(MaintenanceRoot, "staging");
    private static readonly string BackupRoot = Path.Combine(MaintenanceRoot, "backup");
    private static readonly string TrashRoot = Path.Combine(MaintenanceRoot, "trash");

    /// <summary>插件数据根目录（与 PluginSettingsStore 一致：%LocalAppData%\SMTCPlayer\plugins-data）。</summary>
    private static readonly string PluginsDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMTCPlayer", "plugins-data");

    private readonly PluginManager _manager;

    public PluginInstaller(PluginManager manager) => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <summary>
    /// 判定插件目录是否位于 exe 旁的内置插件目录（AppContext.BaseDirectory\plugins）之下。
    /// 内置插件随应用发布，不支持卸载，仅可禁用。
    /// </summary>
    public static bool IsBuiltIn(string pluginDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory)) return false;
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "plugins"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dir = Path.GetFullPath(pluginDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // 非法路径不按内置处理
        }
    }

    // ============== 安装 / 升级 ==============

    /// <summary>
    /// 从 zip 安装包安装/升级插件：
    /// 解压到暂存区并完成全部校验（不触碰已装版本）→ 冲突判定 →
    /// 落位到用户插件目录 → RescanAsync 热激活 → 失败自动回滚旧版本。
    /// 任何失败路径都会清理暂存区，不留半成品。
    /// </summary>
    public async Task<PluginInstallResult> InstallFromZipAsync(
        string zipPath, PluginInstallOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PluginInstallOptions();

        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return Fail(PluginInstallStatus.Failed, $"安装包不存在或不可读: {zipPath}");

        var stagingDir = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            ct.ThrowIfCancellationRequested();

            // 1) 解压到暂存区（路径穿越防护 + 跳过 __MACOSX 等垃圾条目）
            Directory.CreateDirectory(stagingDir);
            try
            {
                ExtractSafely(zipPath, stagingDir);
            }
            catch (Exception ex)
            {
                return Fail(PluginInstallStatus.InvalidPackage, $"安装包无法解压或含非法内容: {ex.Message}");
            }

            // 2) 定位插件根：zip 根直接含 plugin.json，或唯一一个含 plugin.json 的子目录
            var (pluginRoot, locateError) = LocatePluginRoot(stagingDir);
            if (pluginRoot == null)
                return Fail(PluginInstallStatus.InvalidPackage, locateError ?? "压缩包内未找到 plugin.json");

            // 3) 清单与包体校验（全部在暂存区完成，任何失败都不影响已装版本）
            PluginManifest manifest;
            try
            {
                manifest = ReadAndValidateManifest(pluginRoot);
            }
            catch (Exception ex)
            {
                return Fail(PluginInstallStatus.InvalidPackage, ex.Message);
            }

            var incompatible = GetIncompatibilityReason(manifest);
            if (incompatible != null)
                return Fail(PluginInstallStatus.Incompatible, incompatible);

            // 4) 冲突判定（查已登记插件与目标目录）
            var id = manifest.Id;
            var targetDir = Path.Combine(PluginManager.UserPluginsRoot, id);
            var existing = FindPlugin(id);
            var replacing = existing != null || Directory.Exists(targetDir);

            var oldVersion = existing?.Version ?? TryReadInstalledVersion(targetDir);
            var oldDir = existing != null && Directory.Exists(existing.Directory) ? existing.Directory : targetDir;

            if (replacing)
            {
                if (IsBuiltIn(oldDir))
                    return Fail(PluginInstallStatus.IdConflict,
                        $"Id \"{id}\" 已被内置插件占用，内置插件不能被覆盖安装（仅可禁用）");
                if (string.Equals(oldVersion, manifest.Version, StringComparison.OrdinalIgnoreCase)
                    && !options.OverwriteSameVersion)
                    return Fail(PluginInstallStatus.IdConflict,
                        $"已安装相同版本 v{manifest.Version}，如需覆盖请确认");
            }

            ct.ThrowIfCancellationRequested();

            // 5) 落盘：全新安装或升级/覆盖（后者含备份与失败回滚）。
            // 子目录布局的包移动的是定位到的插件根（pluginRoot），staging 里其余内容随 finally 清理。
            return replacing
                ? await ReplaceExistingAsync(id, oldVersion, manifest.Version, oldDir, targetDir, pluginRoot)
                : await InstallFreshAsync(id, manifest.Version, targetDir, pluginRoot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"安装插件包失败: {zipPath}", ex);
            return Fail(PluginInstallStatus.Failed, $"安装失败: {ex.Message}");
        }
        finally
        {
            // 任何路径都不留暂存残骸（成功/回滚路径目录已被 Move 走，此处为空操作）
            TryDeleteDirectory(stagingDir, warnOnly: true);
        }
    }

    /// <summary>全新安装：插件根目录落位 → 重扫激活 → 失败清理现场（无备份可回滚）。</summary>
    private async Task<PluginInstallResult> InstallFreshAsync(
        string pluginId, string newVersion, string targetDir, string pluginRoot)
    {
        var installed = false;
        try
        {
            Directory.CreateDirectory(PluginManager.UserPluginsRoot);
            await MoveDirectoryAsync(pluginRoot, targetDir);
            installed = true;

            await _manager.RescanAsync();

            var info = FindPlugin(pluginId);
            var failure = GetActivationFailure(info, pluginId);
            if (failure == null)
            {
                Log.Info($"插件安装成功: {pluginId} v{newVersion}");
                return new PluginInstallResult
                {
                    Status = PluginInstallStatus.Installed,
                    Plugin = info,
                    NewVersion = newVersion,
                };
            }

            Log.Warn($"插件 {pluginId} v{newVersion} 首次安装激活失败: {failure}");
            return await RollbackAsync(pluginId, oldVersion: null, newVersion, targetDir, backupDir: null,
                installed, failure);
        }
        catch (Exception ex)
        {
            Log.Error($"插件 {pluginId} 安装流程异常", ex);
            return await RollbackAsync(pluginId, null, newVersion, targetDir, null, installed,
                $"安装过程异常: {ex.Message}");
        }
    }

    /// <summary>升级/同版本覆盖：卸载旧版 → 旧目录备份 → 新版落位 → 重扫激活 → 失败自动回滚。</summary>
    private async Task<PluginInstallResult> ReplaceExistingAsync(
        string pluginId, string? oldVersion, string newVersion, string oldDir, string targetDir, string pluginRoot)
    {
        string? backupDir = null;
        var installed = false;
        try
        {
            // a) 旧版本先卸载并忘掉记录（内部走完整卸载序列，保持 plugins.json 启用状态不变；
            //    重扫走"新发现"路径，按原启用状态重新激活）
            await _manager.ForgetPlugin(pluginId);

            // b) 旧目录 → 备份（每 Id 仅保留最近 1 份）
            if (Directory.Exists(oldDir))
            {
                backupDir = Path.Combine(BackupRoot, pluginId, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}");
                Directory.CreateDirectory(Path.GetDirectoryName(backupDir)!); // Move 要求目标父目录已存在
                await MoveDirectoryAsync(oldDir, backupDir);
                await PruneBackupsAsync(pluginId, backupDir);
            }

            // c) 新版本落位
            Directory.CreateDirectory(PluginManager.UserPluginsRoot);
            await MoveDirectoryAsync(pluginRoot, targetDir);
            installed = true;

            // d) 重扫驱动激活
            await _manager.RescanAsync();

            // e) 验证激活结果（插件原本处于禁用状态且无错误时，保持禁用不算失败）
            var info = FindPlugin(pluginId);
            var failure = GetActivationFailure(info, pluginId);
            if (failure == null)
            {
                var status = string.Equals(oldVersion, newVersion, StringComparison.OrdinalIgnoreCase)
                    ? PluginInstallStatus.Overwritten
                    : PluginInstallStatus.Upgraded;
                Log.Info($"插件已{(status == PluginInstallStatus.Overwritten ? "覆盖" : "升级")}: {pluginId} v{oldVersion} → v{newVersion}");
                return new PluginInstallResult
                {
                    Status = status,
                    Plugin = info,
                    OldVersion = oldVersion,
                    NewVersion = newVersion,
                };
            }

            // f) 激活失败 → 回滚
            return await RollbackAsync(pluginId, oldVersion, newVersion, targetDir, backupDir, installed, failure);
        }
        catch (Exception ex)
        {
            Log.Error($"插件 {pluginId} 升级/覆盖流程异常", ex);
            return await RollbackAsync(pluginId, oldVersion, newVersion, targetDir, backupDir, installed,
                $"安装过程异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 失败回滚：坏的新版本移入回收站，备份的旧版本复位并重扫恢复运行；
    /// 无备份（全新安装失败）则仅清理现场。返回带 RolledBack 标记的失败结果。
    /// </summary>
    private async Task<PluginInstallResult> RollbackAsync(
        string pluginId, string? oldVersion, string newVersion,
        string targetDir, string? backupDir, bool newInstalled, string reason)
    {
        var rolledBack = false;
        try
        {
            // 坏新版本进回收站（仅清理本次安装落位的内容）
            if (newInstalled && Directory.Exists(targetDir))
                await MoveToTrashAsync(targetDir);

            // 旧版本复位
            if (backupDir != null && Directory.Exists(backupDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(PluginManager.UserPluginsRoot);
                await MoveDirectoryAsync(backupDir, targetDir);
                rolledBack = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"插件 {pluginId} 回滚清理未完全成功: {ex.Message}");
        }

        // 移除失败记录并重扫：有备份则旧版本重新上线，无备份则插件彻底移除
        try
        {
            await _manager.ForgetPlugin(pluginId);
            await _manager.RescanAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"插件 {pluginId} 回滚后刷新异常: {ex.Message}");
        }

        var restoredVersion = string.IsNullOrWhiteSpace(oldVersion) ? "未知版本" : $"v{oldVersion}";
        var error = rolledBack
            ? $"新版本激活失败已回滚至 {restoredVersion}: {reason}"
            : $"新版本激活失败（无旧版本可回滚）: {reason}";
        Log.Warn($"插件 {pluginId} v{newVersion} {error}");
        return new PluginInstallResult
        {
            Status = PluginInstallStatus.Failed,
            Plugin = FindPlugin(pluginId),
            OldVersion = oldVersion,
            NewVersion = newVersion,
            Error = error,
            RolledBack = rolledBack,
        };
    }

    // ============== 卸载 ==============

    /// <summary>
    /// 卸载用户插件：若已加载先走完整卸载序列并从宿主列表移除，目录移入回收站并删除；
    /// removeData=true 时同时删除插件数据目录并清除 plugins.json 中的禁用记录（彻底清除）。
    /// 内置插件（exe 旁 plugins\ 下）拒绝卸载。
    /// </summary>
    public async Task<PluginUninstallResult> UninstallAsync(
        string pluginId, bool removeData, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            return new PluginUninstallResult { Success = false, Error = "插件 Id 为空" };

        var info = FindPlugin(pluginId);
        if (info == null)
            return new PluginUninstallResult { Success = false, Error = $"未找到插件: {pluginId}" };

        // 内置插件随应用发布，只可禁用不可卸载
        if (IsBuiltIn(info.Directory))
            return new PluginUninstallResult { Success = false, Error = $"内置插件不支持卸载，仅可禁用: {pluginId}" };

        try
        {
            ct.ThrowIfCancellationRequested();

            // 已加载先卸载并移除记录（保持 plugins.json 原状；removeData=true 时同时清除禁用记录）。
            // 不走 SetPluginEnabledAsync(false)：那会在 plugins.json 写入禁用记录，破坏"卸载后重装恢复原状"的语义。
            await _manager.ForgetPlugin(pluginId, removeRegistryState: removeData);

            // 目录 → 回收站并尽快删除；删除失败不影响结果（回收目录在扫描根外，即使残留也不会被重新发现为插件）
            if (Directory.Exists(info.Directory))
                await MoveToTrashAsync(info.Directory);

            // removeData：删除插件数据目录（与 PluginSettingsStore 同路径）
            if (removeData)
            {
                var dataDir = Path.Combine(PluginsDataRoot, pluginId);
                TryDeleteDirectory(dataDir, warnOnly: true);
            }

            // 刷新宿主列表（记录已由 ForgetPlugin 移除——RescanAsync 只加不减）
            await _manager.RescanAsync();

            Log.Info($"插件已卸载: {pluginId}（数据{(removeData ? "已删除" : "保留")}）");
            return new PluginUninstallResult { Success = true };
        }
        catch (Exception ex)
        {
            Log.Error($"卸载插件 {pluginId} 异常", ex);
            return new PluginUninstallResult { Success = false, Error = $"卸载失败: {ex.Message}" };
        }
    }

    // ============== 校验辅助 ==============

    /// <summary>
    /// 解压安装包到暂存目录。显式拒绝绝对路径 / 盘符 / ".." 穿越条目（不依赖 ZipFile 内建防护），
    /// 跳过 __MACOSX / .DS_Store 等打包垃圾；解压后再枚举全部落盘产物，确认均在暂存目录内。
    /// </summary>
    private static void ExtractSafely(string zipPath, string stagingDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var entryName = entry.FullName;
            if (IsJunkEntry(entryName)) continue;

            if (entryName.Contains(':') || Path.IsPathRooted(entryName) || ContainsParentSegment(entryName))
                throw new InvalidOperationException($"压缩包内含非法路径条目: {entryName}");

            var targetPath = Path.GetFullPath(Path.Combine(stagingDir, entryName));
            if (string.Equals(targetPath, Path.GetFullPath(stagingDir).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                continue; // 形如 "." 的目录条目，无实际内容
            if (!IsInside(targetPath, stagingDir))
                throw new InvalidOperationException($"压缩包条目越界: {entryName}");

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(targetPath); // 目录条目（名以 / 结尾）
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }

        // 解压后兜底校验：所有落盘产物都在暂存目录内
        foreach (var file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            if (!IsInside(file, stagingDir))
                throw new InvalidOperationException($"解压产物越界: {file}");
        }
    }

    /// <summary>macOS/打包工具产生的垃圾条目（资源分支目录、Finder 元数据）。</summary>
    private static bool IsJunkEntry(string entryName)
    {
        var segments = entryName.Split('/', '\\');
        return segments.Any(s => s is "__MACOSX" or ".DS_Store");
    }

    /// <summary>定位插件根：zip 根直接含 plugin.json，或唯一一个含 plugin.json 的子目录。</summary>
    private static (string? Root, string? Error) LocatePluginRoot(string stagingDir)
    {
        if (File.Exists(Path.Combine(stagingDir, "plugin.json")))
            return (stagingDir, null);

        var candidates = Directory.EnumerateDirectories(stagingDir)
            .Where(d => File.Exists(Path.Combine(d, "plugin.json")))
            .ToList();

        return candidates.Count switch
        {
            1 => (candidates[0], null),
            0 => (null, "压缩包内未找到 plugin.json"),
            _ => (null, "压缩包内存在多个包含 plugin.json 的目录，无法确定插件根"),
        };
    }

    /// <summary>读取并校验清单（与 PluginManager.LoadFromDirectoryAsync 相同的必填标准 + Id 目录安全约束）。</summary>
    private static PluginManifest ReadAndValidateManifest(string pluginRoot)
    {
        var manifestPath = Path.Combine(pluginRoot, "plugin.json");
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"plugin.json 解析失败: {ex.Message}");
        }
        if (manifest == null
            || string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.Assembly)
            || string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            throw new InvalidOperationException("plugin.json 无效：缺少必填字段 id / assembly / entryType");
        }

        if (!IsValidPluginId(manifest.Id))
            throw new InvalidOperationException(
                $"插件 Id 非法: \"{manifest.Id}\"（只允许字母、数字、-、_、.，长度 1~64，且不得以 . 开头）");

        var asmPath = ResolveAssemblyPath(pluginRoot, manifest.Assembly);
        if (!File.Exists(asmPath))
            throw new InvalidOperationException($"插件程序集不存在: {manifest.Assembly}");

        return manifest;
    }

    /// <summary>Id 即安装目录名：约束字符集与长度，杜绝路径穿越与无法创建的目录名（. 开头 / Windows 保留设备名）。</summary>
    private static bool IsValidPluginId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id[0] == '.')
            return false;
        foreach (var ch in id)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '_' && ch != '.')
                return false;
        }
        return !ReservedDeviceNames.Contains(id.ToUpperInvariant());
    }

    /// <summary>Windows 保留设备名（作为目录名会创建失败或产生奇异行为）。</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>校验程序集相对路径并解析到插件根内的绝对路径（拒绝绝对路径 / 盘符 / .. 越界）。</summary>
    private static string ResolveAssemblyPath(string pluginRoot, string assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly) || assembly.Contains(':') || Path.IsPathRooted(assembly)
            || ContainsParentSegment(assembly))
            throw new InvalidOperationException($"插件程序集路径非法: {assembly}");

        var full = Path.GetFullPath(Path.Combine(pluginRoot, assembly));
        if (!IsInside(full, pluginRoot))
            throw new InvalidOperationException($"插件程序集路径越界: {assembly}");
        return full;
    }

    private static bool ContainsParentSegment(string path) =>
        path.Split('/', '\\').Any(s => s == "..");

    /// <summary>路径是否严格位于 root 之内（大小写不敏感，Windows 文件系统语义）。</summary>
    private static bool IsInside(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 版本门槛校验（与 PluginManager.ActivateAsync 相同的标准与文案）：返回 null 表示兼容。
    /// </summary>
    private static string? GetIncompatibilityReason(PluginManifest manifest)
    {
        if (manifest.ApiVersion is int requiredApi && requiredApi > PluginApiVersion.Current)
            return $"需要插件 API v{requiredApi}，当前应用为 v{PluginApiVersion.Current}，请升级应用";

        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion)
            && Version.TryParse(manifest.MinHostVersion.TrimStart('v', 'V'), out var minHost)
            && HostVersion < minHost)
        {
            return $"需要宿主 v{minHost} 或更高，当前为 v{HostVersion}";
        }
        return null;
    }

    /// <summary>宿主应用版本（与 PluginManager 的门槛校验同源：入口 exe 程序集版本，回落本程序集）。</summary>
    private static Version HostVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0);

    /// <summary>
    /// 重扫后验证激活结果：未被发现 / 启用但加载失败 → 返回原因；
    /// 加载成功或保持禁用（无错误）→ null（成功）。
    /// </summary>
    private static string? GetActivationFailure(PluginInfo? info, string pluginId)
    {
        if (info == null) return $"重扫后未发现插件 {pluginId}（请查看宿主日志）";
        if (!info.IsEnabled) return null; // 原本就是禁用状态：装完保持禁用不算失败
        if (info.IsLoaded && info.Error == null) return null;
        return info.Error ?? $"插件 {pluginId} 未被加载（原因未知，请查看宿主日志）";
    }

    /// <summary>尽力读取已装目录的版本号（目录存在但未登记时用于冲突判定；失败返回 null）。</summary>
    private static string? TryReadInstalledVersion(string dir)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(Path.Combine(dir, "plugin.json")));
            return string.IsNullOrWhiteSpace(manifest?.Version) ? null : manifest!.Version;
        }
        catch
        {
            return null;
        }
    }

    private PluginInfo? FindPlugin(string pluginId) =>
        _manager.Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    private static PluginInstallResult Fail(PluginInstallStatus status, string error) =>
        new() { Status = status, Error = error };

    // ============== 文件系统辅助 ==============

    /// <summary>
    /// 目录改名（同卷内即原子生效）。刚卸载的插件程序集可能仍被内存映射短暂锁定，
    /// 每次失败强制 GC + 等待终结器后重试；多次仍失败则抛出由调用方处理。
    /// </summary>
    private static async Task MoveDirectoryAsync(string sourceDir, string targetDir)
    {
        if (Directory.Exists(targetDir))
            throw new InvalidOperationException($"目标目录已存在: {targetDir}");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(sourceDir, targetDir);
                return;
            }
            catch (Exception ex) when (attempt < 15 && ex is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(targetDir))
                    throw new InvalidOperationException($"目标目录已存在: {targetDir}", ex);
                Log.Debug($"目录改名被占用，GC 后重试 ({attempt}): {sourceDir} → {targetDir}: {ex.Message}");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(200);
            }
        }
    }

    /// <summary>把目录移入回收站并立即尝试删除；删除失败仅告警（回收目录在扫描根外，不会被重新发现为插件）。</summary>
    private static async Task MoveToTrashAsync(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var trashDir = Path.Combine(TrashRoot, name, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(Path.GetDirectoryName(trashDir)!); // Move 要求目标父目录已存在
        await MoveDirectoryAsync(dir, trashDir);
        if (await TryDeleteDirectoryAsync(trashDir, warnOnly: true))
        {
            // 顺手清掉空的按 Id 分组目录，避免维护目录积累空壳
            try
            {
                var idRoot = Path.GetDirectoryName(trashDir)!;
                if (Directory.Exists(idRoot) && !Directory.EnumerateFileSystemEntries(idRoot).Any())
                    Directory.Delete(idRoot);
            }
            catch { /* 清理失败无碍 */ }
        }
    }

    /// <summary>
    /// 删除该 Id 除当前备份外的所有旧备份（只保留最近 1 份）。
    /// 刚被取代的备份其程序集可能仍被尚未回收的 ALC 内存映射锁定，故走带占用重试的删除。
    /// </summary>
    private static async Task PruneBackupsAsync(string pluginId, string keepDir)
    {
        try
        {
            var idBackupRoot = Path.Combine(BackupRoot, pluginId);
            if (!Directory.Exists(idBackupRoot)) return;
            // 先物化待删列表，避免跨 await 枚举时目录被删除导致枚举异常
            var stale = Directory.EnumerateDirectories(idBackupRoot)
                .Where(dir => !string.Equals(dir, keepDir, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var dir in stale)
                await TryDeleteDirectoryAsync(dir, warnOnly: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理插件 {pluginId} 旧备份异常: {ex.Message}");
        }
    }

    /// <summary>尽力删除目录；warnOnly=true 时失败仅告警不抛出。目录本就不存在视为成功。</summary>
    private static bool TryDeleteDirectory(string dir, bool warnOnly)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            if (warnOnly)
            {
                Log.Warn($"删除目录失败（已忽略）: {dir}: {ex.Message}");
                return false;
            }
            throw;
        }
    }

    /// <summary>
    /// 尽力删除目录（带占用重试）：刚卸载的插件程序集可能仍被内存映射短暂锁定，
    /// 与 MoveDirectoryAsync 同理，遇到占用类异常强制 GC + 等待终结器后重试；
    /// warnOnly=true 时最终仍失败仅告警不抛出。目录本就不存在视为成功。
    /// </summary>
    private static async Task<bool> TryDeleteDirectoryAsync(string dir, bool warnOnly)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex) when (attempt < 15 && ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"删除目录被占用，GC 后重试 ({attempt}): {dir}: {ex.Message}");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                if (warnOnly)
                {
                    Log.Warn($"删除目录失败（已忽略）: {dir}: {ex.Message}");
                    return false;
                }
                throw;
            }
        }
    }
}

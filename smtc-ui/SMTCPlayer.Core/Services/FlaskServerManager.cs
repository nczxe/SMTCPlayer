using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SMTCPlayer.Core.Services;

public class FlaskServerManager : IDisposable
{
    private Process? _flaskProcess;
    private readonly string _pythonPath;
    private readonly string _serverDir;
    private StringBuilder _stderrBuffer = new();
    private bool _stopping;

    public bool IsRunning => _flaskProcess != null && !_flaskProcess.HasExited;
    public string LastError { get; private set; } = "";

    private static bool IsFlaskNoise(string line)
    {
        if (line.Contains(" HTTP/1.1\" ") && line.Contains(" - - ["))
            return true;
        if (line.StartsWith("WARNING: This is a development server"))
            return true;
        if (line.StartsWith(" * Running on"))
            return true;
        if (line.StartsWith("Press CTRL+C"))
            return true;
        return false;
    }

    public FlaskServerManager(string pythonPath, string serverDir)
    {
        _pythonPath = pythonPath;
        _serverDir = serverDir;
        Logger.Info($"FlaskServerManager 创建: python={pythonPath}, server={serverDir}");
    }

    public string ValidatePaths()
    {
        Logger.Info("开始路径验证...");

        if (string.IsNullOrWhiteSpace(_pythonPath) || _pythonPath == "python")
        {
            Logger.Info("Python 路径为空，尝试 PATH 中的 python...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var p = Process.Start(psi);
                if (p == null)
                {
                    Logger.Error("PATH 中未找到可用的 Python");
                    return "未找到 Python，请安装 Python 3.11+ 并添加到 PATH";
                }
                var version = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(3000))
                {
                    try { p.Kill(); } catch { }
                    Logger.Error("PATH 中的 Python 检测超时");
                    return "未找到 Python（检测超时），请安装 Python 3.11+ 并添加到 PATH";
                }
                if (p.ExitCode != 0)
                {
                    Logger.Error("PATH 中的 Python 退出码非 0");
                    return "未找到 Python，请安装 Python 3.11+ 并添加到 PATH";
                }
                Logger.Info($"PATH 中找到 Python: {version.Trim()}");
            }
            catch (Exception ex)
            {
                Logger.Error("启动 PATH 中的 Python 失败", ex);
                return "未找到 Python，请安装 Python 3.11+ 并添加到 PATH";
            }
        }
        else if (!File.Exists(_pythonPath))
        {
            Logger.Error($"Python 路径不存在: {_pythonPath}");
            return $"Python 路径不存在: {_pythonPath}";
        }
        else
        {
            Logger.Info($"Python 路径验证通过: {_pythonPath}");
        }

        if (string.IsNullOrWhiteSpace(_serverDir) || !Directory.Exists(_serverDir))
        {
            Logger.Error($"服务目录不存在: {_serverDir}");
            return $"服务目录不存在: {_serverDir}";
        }
        Logger.Info($"服务目录验证通过: {_serverDir}");

        var appPy = Path.Combine(_serverDir, "app.py");
        if (!File.Exists(appPy))
        {
            Logger.Error($"未找到 app.py: {appPy}");
            return $"未找到 app.py: {appPy}";
        }
        Logger.Info($"app.py 验证通过: {appPy}");

        Logger.Info("路径验证全部通过");
        return "";
    }

    public async Task StartAsync(int port)
    {
        _stderrBuffer.Clear();
        LastError = "";

        var appPy = Path.Combine(_serverDir, "app.py");
        Logger.Info($"启动 Flask: {_pythonPath} \"{appPy}\" --port {port}");
        Logger.Info($"工作目录: {_serverDir}");

        _flaskProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{appPy}\" --port {port}",
                WorkingDirectory = _serverDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };
        _flaskProcess.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        _flaskProcess.StartInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

        _flaskProcess.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Debug.WriteLine($"[Flask] {e.Data}");
                Logger.Info($"[Flask] {e.Data}");
            }
        };
        _flaskProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Debug.WriteLine($"[Flask:ERR] {e.Data}");
                if (IsFlaskNoise(e.Data))
                    Logger.Info($"[Flask] {e.Data}");
                else
                {
                    Logger.Warn($"[Flask:ERR] {e.Data}");
                    _stderrBuffer.AppendLine(e.Data);
                }
            }
        };

        _flaskProcess.Exited += (s, e) =>
        {
            if (_stopping)
            {
                Logger.Info($"Flask 进程已停止 (退出码: {_flaskProcess.ExitCode})");
            }
            else
            {
                LastError = _stderrBuffer.ToString();
                Logger.Warn($"Flask 进程意外退出，退出码: {_flaskProcess.ExitCode}");
                if (!string.IsNullOrEmpty(LastError))
                    Logger.Error($"Flask stderr: {LastError}");
            }
        };

        try
        {
            _flaskProcess.Start();
            Logger.Info($"Flask 进程已启动, PID: {_flaskProcess.Id}");
        }
        catch (Exception ex)
        {
            Logger.Error("启动 Flask 进程失败", ex);
            throw new InvalidOperationException($"启动 Python 进程失败: {ex.Message}", ex);
        }

        _flaskProcess.BeginOutputReadLine();
        _flaskProcess.BeginErrorReadLine();

        // Wait up to 3 seconds for the process to stay alive
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (_flaskProcess.HasExited)
            {
                LastError = _stderrBuffer.ToString();
                if (string.IsNullOrEmpty(LastError))
                    LastError = $"Python 进程立即退出，退出码: {_flaskProcess.ExitCode}";
                Logger.Error($"Flask 进程启动后立即退出: {LastError}");
                throw new InvalidOperationException(LastError);
            }
        }

        Logger.Info("Flask 进程存活超过 3 秒，启动成功");
    }

    public static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 80);
            var endpoint = (IPEndPoint)socket.LocalEndPoint!;
            return endpoint.Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public void Stop()
    {
        _stopping = true;
        try
        {
            if (_flaskProcess != null && !_flaskProcess.HasExited)
            {
                Logger.Info($"正在停止 Flask 进程 (PID: {_flaskProcess.Id})...");
                _flaskProcess.Kill(entireProcessTree: true);
                _flaskProcess.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"停止 Flask 进程时出错: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _flaskProcess?.Dispose();
    }
}

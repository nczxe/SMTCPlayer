using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SMTCPlayer.Core.Services;

/// <summary>
/// 子进程托管作业对象（Job Object）。
///
/// 把服务端 / 监视器等子进程挂到本进程的 Job 上并启用 KILL_ON_JOB_CLOSE：
/// 无论主进程是正常退出、崩溃还是被任务管理器强杀，操作系统都会保证
/// 所有子进程随之终结，从机制上杜绝孤儿进程（显式 Stop 仍是第一优先路径，
/// 本类只是兜底保险）。
/// </summary>
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private static readonly object Gate = new();
    private static IntPtr _jobHandle;
    private static bool _initFailed;

    /// <summary>把指定子进程加入托管作业。失败静默忽略（不影响功能，仅失去兜底）。</summary>
    public static void Assign(Process child)
    {
        try
        {
            Assign(child.Handle);
        }
        catch
        {
            // 句柄不可用（已退出等），忽略
        }
    }

    public static void Assign(IntPtr childProcessHandle)
    {
        if (childProcessHandle == IntPtr.Zero) return;

        lock (Gate)
        {
            if (_initFailed) return;
            if (_jobHandle == IntPtr.Zero && !TryCreateJob())
            {
                _initFailed = true;
                return;
            }
            AssignProcessToJobObject(_jobHandle, childProcessHandle);
        }
    }

    private static bool TryCreateJob()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return false;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation,
                ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(handle);
            return false;
        }

        _jobHandle = handle;
        return true;
    }

    // ---- Win32 ----

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

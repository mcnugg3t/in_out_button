using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InOutButton;

/// <summary>
/// a windows job object that kills every process still in it when its handle closes.
/// Kill(entireProcessTree: true) misses a grandchild once its parent has exited; the job doesn't
/// (docs/2026-10-18-burn-pace-gemini-token-spec.md, section 6.1)
/// </summary>
internal sealed class ChildJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static int _failureLogged;

    private readonly JobHandle _handle;

    private ChildJob(JobHandle handle)
    {
        _handle = handle;
    }

    // win32 error from the last failed TryAssign, for the caller's log
    public int LastError { get; private set; }

    /// <summary>
    /// null when windows refuses the job, so the caller runs the child without one as before.
    /// only the first failure in the process reaches log
    /// </summary>
    public static ChildJob? TryCreate(Action<string>? log = null)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            return Fail(handle, "CreateJobObject", log);
        }

        var info = new ExtendedLimitInformation
        {
            Basic = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            return Fail(handle, "SetInformationJobObject", log);
        }

        return new ChildJob(handle);
    }

    /// <summary>
    /// assign right after Start: only processes the child starts after this land in the job
    /// </summary>
    public bool TryAssign(Process process)
    {
        try
        {
            if (AssignProcessToJobObject(_handle, process.SafeHandle))
            {
                return true;
            }

            LastError = Marshal.GetLastPInvokeError();
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
            // never started, already disposed, or the job is closed
            return false;
        }
    }

    // closing the last handle kills everything still in the job
    public void Dispose() => _handle.Dispose();

    private static ChildJob? Fail(JobHandle handle, string call, Action<string>? log)
    {
        // read before Dispose, whose CloseHandle resets it
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (log is not null && Interlocked.Exchange(ref _failureLogged, 1) == 0)
        {
            log($"child job: {call} failed with win32 error {error}; child processes run without a job");
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern JobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    // JOBOBJECT_BASIC_LIMIT_INFORMATION; only LimitFlags is set, the rest pads the layout
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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

    // IO_COUNTERS
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

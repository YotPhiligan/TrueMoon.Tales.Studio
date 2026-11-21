using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TrueMoon.Tales.Studio.Backend.Export;

[SupportedOSPlatform("windows")]
internal static class WindowsExportJob
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    // The worker cannot start FFmpeg until the owner sends START, after assignment here.
    internal static SafeFileHandle Attach(Process process)
    {
        var job = CreateJobObjectW(0, 0);
        try
        {
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var limits = new ExtendedLimits { Basic = new() { LimitFlags = KillOnJobClose } };
            if (SetInformationJobObject(job, ExtendedLimitInformation, in limits, (uint)Marshal.SizeOf<ExtendedLimits>()) == 0 ||
                AssignProcessToJobObject(job, process.SafeHandle) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, nint name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SetInformationJobObject(SafeFileHandle job, int informationClass, in ExtendedLimits information, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}

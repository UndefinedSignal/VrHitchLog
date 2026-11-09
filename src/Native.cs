using System.Runtime.InteropServices;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace VrHitchLog;

internal static class Native
{
    // ---- threads / timers ----
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] public static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);
    [DllImport("kernel32.dll")] public static extern bool SetThreadPriority(IntPtr hThread, int priority);
    public const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    [DllImport("ntdll.dll")] public static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    // ---- process list ----
    [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
    public const int SystemProcessInformation = 5;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    // ---- PDH ----
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll")] public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }
}

internal static class Clock
{
    public static readonly long Freq = System.Diagnostics.Stopwatch.Frequency;
    public static readonly long Start = System.Diagnostics.Stopwatch.GetTimestamp();
    public static readonly DateTime StartWall = DateTime.Now;

    /// <summary>Seconds since session start.</summary>
    public static double Now => (System.Diagnostics.Stopwatch.GetTimestamp() - Start) / (double)Freq;
    public static double ToT(long qpc) => (qpc - Start) / (double)Freq;
    /// <summary>Absolute QPC in seconds (seconds since boot on Windows).</summary>
    public static double QpcSeconds => System.Diagnostics.Stopwatch.GetTimestamp() / (double)Freq;
    public static double StartQpcSeconds => Start / (double)Freq;
    public static string Wall(double t) => StartWall.AddSeconds(t).ToString("HH:mm:ss.fff");
}

using System.Runtime.InteropServices;
using static VrHitchLog.Native;

namespace VrHitchLog;

internal sealed record ProcDelta(int Pid, string Name, double CpuMs, double CyclesM, long HardFaults, long PageFaults,
    double ReadMb, double WriteMb, double OtherMb, double PrivateMb, int Threads);

internal sealed record ProcSecond(double T, List<ProcDelta> Top);

/// <summary>
/// Once a second reads all processes with one NtQuerySystemInformation call and writes the busiest ones
/// (CPU, hard page faults, disk I/O) to procs.csv. Only process names, no command lines or paths.
/// </summary>
internal sealed class ProcessSampler
{
    private readonly Csv _csv;
    private readonly Ring<ProcSecond> _ring = new(900);
    public Ring<ProcSecond> Ring => _ring;
    private Dictionary<(int, long), Raw> _prev = new();
    private volatile Dictionary<int, string> _names = new();
    private IntPtr _buf = IntPtr.Zero;
    private int _bufSize = 1 << 20;
    public bool Ok { get; private set; } = true;
    public HashSet<string> SeenNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    private struct Raw
    {
        public string Name; public int Pid; public int Threads;
        public long Cpu100ns; public long Cycles; public long HardFaults; public long PageFaults;
        public long ReadBytes, WriteBytes, OtherBytes; public long Private;
    }

    public ProcessSampler(string dir)
    {
        _csv = new Csv(Path.Combine(dir, "procs.csv"),
            "t,pid,name,cpu_ms,cycles_m,hard_faults,page_faults,read_mb,write_mb,other_io_mb,private_mb,threads");
    }

    public string NameOf(int pid) => _names.TryGetValue(pid, out var n) ? n : "";
    public void Flush() => _csv.Flush();

    /// <summary>Checks that the raw structure layout matches this Windows build by finding our own process.</summary>
    public string SelfCheck()
    {
        var snap = Read();
        int me = Environment.ProcessId;
        var self = snap.Values.FirstOrDefault(r => r.Pid == me);
        if (self.Name == null) { Ok = false; return "НЕ ПРОЙДЕНА: свой процесс не найден"; }
        long priv = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        bool nameOk = self.Name.StartsWith("VrHitchLog", StringComparison.OrdinalIgnoreCase);
        bool privOk = self.Private > priv / 4 && self.Private < priv * 4;
        Ok = nameOk && privOk && self.Threads > 0;
        return Ok ? $"ok ({snap.Count} процессов)" : $"НЕ ПРОЙДЕНА: name={self.Name} private={self.Private} vs {priv}";
    }

    private Dictionary<(int, long), Raw> Read()
    {
        while (true)
        {
            if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(_bufSize);
            int st = NtQuerySystemInformation(SystemProcessInformation, _buf, _bufSize, out int need);
            if (st == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero;
                _bufSize = Math.Max(need + (1 << 16), _bufSize * 2);
                continue;
            }
            if (st != 0) throw new InvalidOperationException($"NtQuerySystemInformation 0x{st:X8}");
            break;
        }

        // SYSTEM_PROCESS_INFORMATION, x64 offsets (phnt):
        //  0 NextEntryOffset  4 NumberOfThreads  16 HardFaultCount  24 CycleTime  32 CreateTime  40 UserTime  48 KernelTime
        // 56 ImageName (USHORT Length, Buffer at 64)  80 UniqueProcessId  128 PageFaultCount  200 PrivatePageCount
        // 232 ReadTransferCount  240 WriteTransferCount  248 OtherTransferCount
        var result = new Dictionary<(int, long), Raw>();
        int offset = 0;
        while (true)
        {
            IntPtr p = _buf + offset;
            int nextOff = Marshal.ReadInt32(p, 0);
            int pid = (int)Marshal.ReadIntPtr(p, 80);
            long create = Marshal.ReadInt64(p, 32);
            int len = (ushort)Marshal.ReadInt16(p, 56);
            IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
            string name = pid == 0 ? "Idle" : (namePtr != IntPtr.Zero && len > 0 ? Marshal.PtrToStringUni(namePtr, len / 2) : "?");
            result[(pid, create)] = new Raw
            {
                Name = name, Pid = pid,
                Threads = Marshal.ReadInt32(p, 4),
                HardFaults = (uint)Marshal.ReadInt32(p, 16),
                Cycles = Marshal.ReadInt64(p, 24),
                Cpu100ns = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48),
                PageFaults = (uint)Marshal.ReadInt32(p, 128),
                Private = (long)Marshal.ReadIntPtr(p, 200),
                ReadBytes = Marshal.ReadInt64(p, 232),
                WriteBytes = Marshal.ReadInt64(p, 240),
                OtherBytes = Marshal.ReadInt64(p, 248),
            };
            if (nextOff == 0) break;
            offset += nextOff;
        }
        return result;
    }

    public void Run(CancellationToken ct)
    {
        long next = System.Diagnostics.Stopwatch.GetTimestamp();
        double prevT = Clock.Now;
        _prev = Read();
        while (!ct.IsCancellationRequested)
        {
            next += Clock.Freq;
            long wait = (next - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 / Clock.Freq;
            if (wait > 0) ct.WaitHandle.WaitOne((int)wait);
            if (ct.IsCancellationRequested) break;
            try { Sample(ref prevT); }
            catch (Exception ex) { Log.File("ProcessSampler: " + ex.Message); }
        }
    }

    private void Sample(ref double prevT)
    {
        var cur = Read();
        double t = Clock.Now;
        double dt = Math.Max(0.001, t - prevT);
        prevT = t;

        var names = new Dictionary<int, string>(cur.Count);
        var deltas = new List<ProcDelta>(cur.Count);
        foreach (var (key, r) in cur)
        {
            names[r.Pid] = r.Name;
            SeenNames.Add(r.Name);
            if (r.Pid == 0) continue; // Idle
            if (!_prev.TryGetValue(key, out var o)) o = new Raw(); // new process: count from zero
            // per-second normalized
            deltas.Add(new ProcDelta(r.Pid, r.Name,
                (r.Cpu100ns - o.Cpu100ns) / 1e4 / dt,
                (r.Cycles - o.Cycles) / 1e6 / dt,
                (long)((r.HardFaults - o.HardFaults) / dt),
                (long)((r.PageFaults - o.PageFaults) / dt),
                (r.ReadBytes - o.ReadBytes) / 1048576.0 / dt,
                (r.WriteBytes - o.WriteBytes) / 1048576.0 / dt,
                (r.OtherBytes - o.OtherBytes) / 1048576.0 / dt,
                r.Private / 1048576.0, r.Threads));
        }
        _names = names;
        _prev = cur;

        var top = new Dictionary<int, ProcDelta>();
        foreach (var d in deltas.OrderByDescending(d => d.CpuMs).Take(10)) top[d.Pid] = d;
        foreach (var d in deltas.Where(d => d.HardFaults > 0).OrderByDescending(d => d.HardFaults).Take(5)) top[d.Pid] = d;
        foreach (var d in deltas.OrderByDescending(d => d.ReadMb + d.WriteMb).Take(5).Where(d => d.ReadMb + d.WriteMb > 0.01)) top[d.Pid] = d;
        foreach (var d in deltas.Where(d => d.PageFaults > 0).OrderByDescending(d => d.PageFaults).Take(3)) top[d.Pid] = d;

        var list = top.Values.OrderByDescending(d => d.CpuMs).ToList();
        foreach (var d in list)
            _csv.Row(t, d.Pid, d.Name, d.CpuMs, d.CyclesM, d.HardFaults, d.PageFaults, d.ReadMb, d.WriteMb, d.OtherMb, d.PrivateMb, d.Threads);
        _ring.Add(new ProcSecond(t, list));
    }
}

using System.Text.RegularExpressions;

namespace VrHitchLog;

internal sealed record SysSample(
    double T, double Cpu, double CpuCoreMax, double Dpc, double DpcCoreMax, int DpcCore,
    double Intr, double IntrCoreMax, int IntrCore, double PagesInput, double DiskMaxLatMs, string DiskMaxLatName,
    double AvailMb);

/// <summary>
/// System-wide counters: CPU / DPC / interrupts (per core), memory and paging, disks — 4 times a second;
/// GPU engines, GPU memory, network, temperature — once a second.
/// </summary>
internal sealed class SystemSampler
{
    private readonly Csv _sys, _disks, _slow;
    private readonly Ring<SysSample> _ring = new(2400); // 10 min at 4 Hz
    public Ring<SysSample> Ring => _ring;
    private readonly ProcessSampler _procs;
    public double LastDpcCoreMax { get; private set; }
    public double LastIntrCoreMax { get; private set; }
    public double WorstDpcCoreMax5s; // reset by status printer

    private static readonly Regex GpuInst = new(@"pid_(\d+)_.*engtype_(.+)$", RegexOptions.Compiled);

    public SystemSampler(string dir, ProcessSampler procs)
    {
        _procs = procs;
        _sys = new Csv(Path.Combine(dir, "sys.csv"),
            "t,cpu,cpu_core_max,cpu_core_max_idx,dpc,dpc_core_max,dpc_core_max_idx,intr,intr_core_max,intr_core_max_idx," +
            "dpcs_per_s,intr_per_s,ctx_switch_per_s,proc_queue,mem_avail_mb,commit_mb,commit_limit_mb," +
            "pages_input_per_s,page_faults_per_s,pages_per_s,disk_max_lat_ms,disk_max_lat_name,disk_max_queue,disk_max_queue_name");
        _disks = new Csv(Path.Combine(dir, "disks.csv"), "t,disk,lat_ms,queue,idle_pct,read_mb_s,write_mb_s");
        _slow = new Csv(Path.Combine(dir, "sys_slow.csv"),
            "t,gpu_3d,gpu_copy,gpu_vdec,gpu_venc,gpu_compute,gpu_other,gpu3d_top_pid,gpu3d_top_name,gpu3d_top_pct," +
            "gpu_dedicated_mb,gpu_shared_mb,net_mb_s,pool_nonpaged_mb,thermal_c_max");
    }

    public void Flush() { _sys.Flush(); _disks.Flush(); _slow.Flush(); }

    public void Run(CancellationToken ct)
    {
        using var fast = new PdhQuery();
        var cpuT = fast.Add(@"\Processor Information(_Total)\% Processor Time");
        var dpcT = fast.Add(@"\Processor Information(_Total)\% DPC Time");
        var intrT = fast.Add(@"\Processor Information(_Total)\% Interrupt Time");
        var cpuA = fast.Add(@"\Processor Information(*)\% Processor Time");
        var dpcA = fast.Add(@"\Processor Information(*)\% DPC Time");
        var intrA = fast.Add(@"\Processor Information(*)\% Interrupt Time");
        var dpcsQ = fast.Add(@"\Processor Information(_Total)\DPCs Queued/sec");
        var intrS = fast.Add(@"\Processor Information(_Total)\Interrupts/sec");
        var ctx = fast.Add(@"\System\Context Switches/sec");
        var pq = fast.Add(@"\System\Processor Queue Length");
        var avail = fast.Add(@"\Memory\Available MBytes");
        var commit = fast.Add(@"\Memory\Committed Bytes");
        var commitLim = fast.Add(@"\Memory\Commit Limit");
        var pagesIn = fast.Add(@"\Memory\Pages Input/sec");
        var pf = fast.Add(@"\Memory\Page Faults/sec");
        var pages = fast.Add(@"\Memory\Pages/sec");
        var dLat = fast.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        var dQ = fast.Add(@"\PhysicalDisk(*)\Current Disk Queue Length");
        var dIdle = fast.Add(@"\PhysicalDisk(*)\% Idle Time");
        var dRd = fast.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        var dWr = fast.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");

        using var slow = new PdhQuery();
        var gpuEng = slow.Add(@"\GPU Engine(*)\Utilization Percentage");
        var gpuDed = slow.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        var gpuSh = slow.Add(@"\GPU Adapter Memory(*)\Shared Usage");
        var net = slow.Add(@"\Network Interface(*)\Bytes Total/sec");
        var pool = slow.Add(@"\Memory\Pool Nonpaged Bytes");
        var therm = slow.Add(@"\Thermal Zone Information(*)\Temperature");

        foreach (var f in fast.Failed.Concat(slow.Failed)) Log.File("PDH counter unavailable: " + f);

        fast.Collect(); slow.Collect();
        long next = System.Diagnostics.Stopwatch.GetTimestamp();
        long period = Clock.Freq / 4;
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            next += period;
            long wait = (next - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 / Clock.Freq;
            if (wait > 0) ct.WaitHandle.WaitOne((int)wait);
            if (ct.IsCancellationRequested) break;
            try
            {
                fast.Collect();
                double t = Clock.Now;
                SampleFast(t, cpuT, dpcT, intrT, cpuA, dpcA, intrA, dpcsQ, intrS, ctx, pq, avail, commit, commitLim, pagesIn, pf, pages, dLat, dQ, dIdle, dRd, dWr);
                if (++tick % 4 == 0)
                {
                    slow.Collect();
                    SampleSlow(Clock.Now, gpuEng, gpuDed, gpuSh, net, pool, therm);
                }
            }
            catch (Exception ex) { Log.File("SystemSampler: " + ex.Message); }
        }
    }

    private static double V(PdhCounter? c) => c?.Value() ?? double.NaN;

    private static (double max, int idx) CoreMax(PdhCounter? c)
    {
        if (c == null) return (double.NaN, -1);
        double max = double.NaN; int idx = -1;
        foreach (var (name, v) in c.Array())
        {
            if (name.Contains("_Total")) continue;
            int comma = name.LastIndexOf(',');
            if (!int.TryParse(comma >= 0 ? name[(comma + 1)..] : name, out int core)) continue;
            if (double.IsNaN(max) || v > max) { max = v; idx = core; }
        }
        return (max, idx);
    }

    private void SampleFast(double t, PdhCounter? cpuT, PdhCounter? dpcT, PdhCounter? intrT, PdhCounter? cpuA, PdhCounter? dpcA,
        PdhCounter? intrA, PdhCounter? dpcsQ, PdhCounter? intrS, PdhCounter? ctx, PdhCounter? pq, PdhCounter? avail,
        PdhCounter? commit, PdhCounter? commitLim, PdhCounter? pagesIn, PdhCounter? pf, PdhCounter? pages,
        PdhCounter? dLat, PdhCounter? dQ, PdhCounter? dIdle, PdhCounter? dRd, PdhCounter? dWr)
    {
        var (cpuMax, cpuIdx) = CoreMax(cpuA);
        var (dpcMax, dpcIdx) = CoreMax(dpcA);
        var (intrMax, intrIdx) = CoreMax(intrA);

        var lat = dLat?.Array() ?? new();
        var q = dQ?.Array().ToDictionary(x => x.Name, x => x.Value) ?? new();
        var idle = dIdle?.Array().ToDictionary(x => x.Name, x => x.Value) ?? new();
        var rd = dRd?.Array().ToDictionary(x => x.Name, x => x.Value) ?? new();
        var wr = dWr?.Array().ToDictionary(x => x.Name, x => x.Value) ?? new();
        double latMax = double.NaN, qMax = double.NaN; string latName = "", qName = "";
        foreach (var (name, v) in lat)
        {
            if (name == "_Total") continue;
            double ms = v * 1000.0;
            double qq = q.GetValueOrDefault(name, double.NaN);
            _disks.Row(t, name, ms, qq, idle.GetValueOrDefault(name, double.NaN),
                rd.GetValueOrDefault(name, double.NaN) / 1048576.0, wr.GetValueOrDefault(name, double.NaN) / 1048576.0);
            if (double.IsNaN(latMax) || ms > latMax) { latMax = ms; latName = name; }
            if (!double.IsNaN(qq) && (double.IsNaN(qMax) || qq > qMax)) { qMax = qq; qName = name; }
        }

        double cpu = V(cpuT), dpc = V(dpcT), intr = V(intrT), pin = V(pagesIn), av = V(avail);
        _sys.Row(t, cpu, cpuMax, cpuIdx, dpc, dpcMax, dpcIdx, intr, intrMax, intrIdx,
            V(dpcsQ), V(intrS), V(ctx), V(pq), av, V(commit) / 1048576.0, V(commitLim) / 1048576.0,
            pin, V(pf), V(pages), latMax, latName, qMax, qName);

        LastDpcCoreMax = dpcMax;
        LastIntrCoreMax = intrMax;
        if (!double.IsNaN(dpcMax) && dpcMax > WorstDpcCoreMax5s) WorstDpcCoreMax5s = dpcMax;
        _ring.Add(new SysSample(t, cpu, cpuMax, dpc, dpcMax, dpcIdx, intr, intrMax, intrIdx, pin, latMax, latName, av));
    }

    private void SampleSlow(double t, PdhCounter? gpuEng, PdhCounter? gpuDed, PdhCounter? gpuSh, PdhCounter? net, PdhCounter? pool, PdhCounter? therm)
    {
        var byType = new Dictionary<string, double>();
        var pid3d = new Dictionary<int, double>();
        if (gpuEng != null)
        {
            foreach (var (name, v) in gpuEng.Array())
            {
                var m = GpuInst.Match(name);
                if (!m.Success) continue;
                string type = m.Groups[2].Value;
                byType[type] = byType.GetValueOrDefault(type) + v;
                if (type == "3D" && int.TryParse(m.Groups[1].Value, out int pid))
                    pid3d[pid] = pid3d.GetValueOrDefault(pid) + v;
            }
        }
        double other = byType.Where(kv => kv.Key is not ("3D" or "Copy" or "VideoDecode" or "VideoEncode" or "Compute_0" or "Compute_1" or "Compute"))
                             .Sum(kv => kv.Value);
        double compute = byType.Where(kv => kv.Key.StartsWith("Compute")).Sum(kv => kv.Value);
        int topPid = -1; double topPct = double.NaN;
        foreach (var (pid, v) in pid3d) if (double.IsNaN(topPct) || v > topPct) { topPct = v; topPid = pid; }

        double ded = gpuDed?.Array().Select(x => x.Value).DefaultIfEmpty(double.NaN).Max() ?? double.NaN;
        double sh = gpuSh?.Array().Select(x => x.Value).DefaultIfEmpty(double.NaN).Max() ?? double.NaN;
        double netSum = net?.Array().Sum(x => x.Value) ?? double.NaN;
        double thermMax = therm?.Array().Select(x => x.Value - 273.15).DefaultIfEmpty(double.NaN).Max() ?? double.NaN;

        _slow.Row(t,
            byType.GetValueOrDefault("3D", double.NaN), byType.GetValueOrDefault("Copy", double.NaN),
            byType.GetValueOrDefault("VideoDecode", double.NaN), byType.GetValueOrDefault("VideoEncode", double.NaN),
            compute, other, topPid, topPid >= 0 ? _procs.NameOf(topPid) : "", topPct,
            ded / 1048576.0, sh / 1048576.0, netSum / 1048576.0, V(pool) / 1048576.0, thermMax);
    }
}

/// <summary>Fixed-size ring buffer, thread-safe.</summary>
internal sealed class Ring<T>
{
    private readonly T[] _items;
    private int _next, _count;
    private readonly object _lock = new();
    public Ring(int capacity) => _items = new T[capacity];

    public void Add(T item)
    {
        lock (_lock)
        {
            _items[_next] = item;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    public List<T> Snapshot()
    {
        lock (_lock)
        {
            var list = new List<T>(_count);
            int start = (_next - _count + _items.Length) % _items.Length;
            for (int i = 0; i < _count; i++) list.Add(_items[(start + i) % _items.Length]);
            return list;
        }
    }
}

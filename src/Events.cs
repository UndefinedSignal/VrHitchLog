namespace VrHitchLog;

internal sealed class HitchEvent
{
    public string Kind = "";        // "stall" (cores stood still) or "frame" (SteamVR frame spike)
    public double T0, T1;           // window, session seconds
    public double DurMs;
    public int Cores;
    public bool AllCores;
    public uint Frame, Dropped, MisPresented, Presents;
    public float ClientIntervalMs;
    public bool DuringWpr;
    public double OverlapMs;        // stall: worst frame interval in the window; frame: worst core stall in the window
    public string? WprFile;
    public string Context = "";
}

/// <summary>Collects stall clusters and frame spikes, cross-matches them and adds system/process context.</summary>
internal sealed class EventDetector
{
    private readonly Csv _csv;
    private readonly double _eventMs, _wprMs;
    private readonly int _probes;
    private readonly SystemSampler _sys;
    private readonly ProcessSampler _procs;
    private readonly Wpr? _wpr;
    private readonly object _lock = new();
    private readonly List<StallCluster> _clusters = new();
    private readonly List<(double T0, double T1, double Ms)> _frameIntervals = new();
    private readonly List<HitchEvent> _pending = new();
    public readonly List<HitchEvent> All = new();
    public int StallEvents5s, FrameEvents5s;

    public EventDetector(string dir, double eventMs, double wprMs, int probes, SystemSampler sys, ProcessSampler procs, Wpr? wpr)
    {
        _eventMs = eventMs; _wprMs = wprMs; _probes = probes; _sys = sys; _procs = procs; _wpr = wpr;
        _csv = new Csv(Path.Combine(dir, "events.csv"),
            "t,wall,kind,dur_ms,cores,all_cores,frame,dropped,mispresented,presents,client_interval_ms,overlap_ms,during_wpr," +
            "cpu,dpc_core_max,intr_core_max,pages_input_s,disk_max_lat_ms,disk_max_lat_name,mem_avail_mb,top_cpu,top_hard_faults,top_io,wpr_file");
    }

    public void Flush() => _csv.Flush();

    private bool WprBusy(double t) => _wpr != null && (_wpr.Busy || t < _wpr.BusyUntil);

    public void OnCluster(StallCluster c)
    {
        lock (_lock)
        {
            _clusters.Add(c);
            if (c.MaxMs < _eventMs) return;
            var e = new HitchEvent
            {
                Kind = "stall", T0 = c.T0, T1 = c.T1, DurMs = c.MaxMs, Cores = c.Cores.Count,
                AllCores = c.Cores.Count >= Math.Max(1, (int)Math.Ceiling(_probes * 0.75)), DuringWpr = WprBusy(c.T0),
            };
            _pending.Add(e);
            StallEvents5s++;
            if (!e.DuringWpr && c.MaxMs >= _wprMs)
                e.WprFile = _wpr?.Request(c.T0, $"stall {c.MaxMs:0} ms on {c.Cores.Count} cores");
        }
    }

    public void OnFrame(FrameEvent f)
    {
        lock (_lock)
        {
            double t0 = f.T - Math.Max(f.IntervalMs, f.ClientIntervalMs) / 1000.0;
            _frameIntervals.Add((t0, f.T, f.IntervalMs));
            var e = new HitchEvent
            {
                Kind = "frame", T0 = t0, T1 = f.T, DurMs = Math.Max(f.IntervalMs, f.ClientIntervalMs), Frame = f.FrameIndex,
                Dropped = f.Dropped, MisPresented = f.MisPresented, Presents = f.Presents, ClientIntervalMs = f.ClientIntervalMs,
                DuringWpr = WprBusy(f.T),
            };
            _pending.Add(e);
            FrameEvents5s++;
            if (!e.DuringWpr && e.DurMs >= _wprMs * 2)
                e.WprFile = _wpr?.Request(f.T, $"frame interval {e.DurMs:0} ms");
        }
    }

    /// <summary>Finalizes events older than 3 s (so frames, stalls and counters around them have arrived).</summary>
    public void Tick(bool final = false)
    {
        List<HitchEvent> ready;
        lock (_lock)
        {
            double now = Clock.Now;
            ready = _pending.Where(e => final || e.T1 < now - 3).ToList();
            foreach (var e in ready) _pending.Remove(e);
            foreach (var e in ready)
            {
                const double tol = 0.03;
                if (e.Kind == "frame")
                    e.OverlapMs = _clusters.Where(c => c.T1 >= e.T0 - tol && c.T0 <= e.T1 + tol).Select(c => c.MaxMs).DefaultIfEmpty(0).Max();
                else
                    e.OverlapMs = _frameIntervals.Where(f => f.T1 >= e.T0 - tol && f.T0 <= e.T1 + tol).Select(f => f.Ms).DefaultIfEmpty(0).Max();
            }
            // keep memory bounded
            if (_clusters.Count > 50000) _clusters.RemoveRange(0, 10000);
            if (_frameIntervals.Count > 50000) _frameIntervals.RemoveRange(0, 10000);
        }
        if (ready.Count == 0) return;

        var sys = _sys.Ring.Snapshot();
        var procs = _procs.Ring.Snapshot();
        foreach (var e in ready.OrderBy(e => e.T0))
        {
            // counters are averages over the preceding 250 ms → take the worst sample covering the event
            var s = sys.Where(x => x.T >= e.T0 && x.T <= e.T1 + 0.5).ToList();
            double M(Func<SysSample, double> f) => s.Count == 0 ? double.NaN : s.Max(f);
            var diskS = s.Where(x => !double.IsNaN(x.DiskMaxLatMs)).OrderByDescending(x => x.DiskMaxLatMs).FirstOrDefault();
            var ps = procs.FirstOrDefault(p => p.T >= e.T1);
            string top = "", hf = "", io = "";
            if (ps != null)
            {
                top = string.Join(" ", ps.Top.OrderByDescending(d => d.CpuMs).Take(4).Select(d => $"{d.Name}:{d.CpuMs:0}"));
                hf = string.Join(" ", ps.Top.Where(d => d.HardFaults > 0).OrderByDescending(d => d.HardFaults).Take(3).Select(d => $"{d.Name}:{d.HardFaults}"));
                io = string.Join(" ", ps.Top.Where(d => d.ReadMb + d.WriteMb > 0.5).OrderByDescending(d => d.ReadMb + d.WriteMb).Take(3).Select(d => $"{d.Name}:{d.ReadMb + d.WriteMb:0.#}"));
            }
            _csv.Row(e.T0, Clock.Wall(e.T0), e.Kind, e.DurMs, e.Kind == "stall" ? e.Cores : null, e.Kind == "stall" ? (e.AllCores ? 1 : 0) : null,
                e.Kind == "frame" ? e.Frame : null, e.Kind == "frame" ? e.Dropped : null, e.Kind == "frame" ? e.MisPresented : null,
                e.Kind == "frame" ? e.Presents : null, e.Kind == "frame" ? e.ClientIntervalMs : null,
                e.OverlapMs, e.DuringWpr ? 1 : 0,
                M(x => x.Cpu), M(x => x.DpcCoreMax), M(x => x.IntrCoreMax), M(x => x.PagesInput),
                diskS?.DiskMaxLatMs ?? double.NaN, diskS?.DiskMaxLatName ?? "", s.Count == 0 ? double.NaN : s.Min(x => x.AvailMb),
                top, hf, io, e.WprFile ?? "");
            lock (_lock) All.Add(e);
        }
    }
}

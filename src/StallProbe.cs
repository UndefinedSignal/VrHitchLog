using System.Collections.Concurrent;
using static VrHitchLog.Native;

namespace VrHitchLog;

internal readonly record struct Gap(long StartQpc, long EndQpc, int Core)
{
    public double Ms => (EndQpc - StartQpc) * 1000.0 / Clock.Freq;
}

internal sealed class StallCluster
{
    public double T0, T1;           // session seconds
    public double MaxMs;
    public readonly HashSet<int> Cores = new();
    public int Gaps;
}

/// <summary>
/// One time-critical thread pinned to each logical core. Every thread sleeps 1 ms in a loop and measures how long
/// it actually was away. A long gap means the core was taken by something above normal threads: DPC/ISR (drivers),
/// realtime threads, or firmware (SMI). Gaps on all cores at once = the whole machine stood still.
/// </summary>
internal sealed class StallProbe
{
    private readonly double _gapMs;
    private readonly double _tolMs;
    private readonly ConcurrentQueue<Gap> _queue = new();
    private readonly List<Thread> _threads = new();
    private readonly Csv _csv;
    public int CoreCount { get; }
    // per-core histogram of gaps: >=4, >=8, >=16, >=32, >=64, >=128 ms
    public static readonly double[] Buckets = { 4, 8, 16, 32, 64, 128 };
    public readonly long[,] Hist;
    public double MaxGap5s; // reset by status printer
    public event Action<StallCluster>? ClusterReady;

    public StallProbe(string dir, double gapMs, int[]? cores)
    {
        _gapMs = gapMs;
        _tolMs = 3;
        CoreCount = Environment.ProcessorCount;
        Hist = new long[CoreCount, Buckets.Length];
        _csv = new Csv(Path.Combine(dir, "stalls.csv"), "t,dur_ms,core");
        foreach (int c in cores ?? Enumerable.Range(0, CoreCount).ToArray())
        {
            if (c < 0 || c >= CoreCount || c >= 64) continue;
            int core = c;
            var th = new Thread(() => Probe(core)) { IsBackground = true, Name = $"probe{core}" };
            _threads.Add(th);
        }
    }

    public int ProbeCount => _threads.Count;

    public void Start(CancellationToken ct)
    {
        _ct = ct;
        foreach (var t in _threads) t.Start();
    }

    private CancellationToken _ct;

    private void Probe(int core)
    {
        Thread.BeginThreadAffinity();
        SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << core));
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        long gapTicks = (long)(_gapMs * Clock.Freq / 1000.0);
        long prev = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!_ct.IsCancellationRequested)
        {
            Thread.Sleep(1);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - prev >= gapTicks) _queue.Enqueue(new Gap(prev, now, core));
            prev = now;
        }
    }

    private StallCluster? _cur;
    private readonly List<Gap> _pending = new();

    /// <summary>Called every ~100 ms from the merger thread: writes raw gaps and groups overlapping ones.</summary>
    public void Drain()
    {
        while (_queue.TryDequeue(out var g)) _pending.Add(g);
        double now = Clock.Now;
        if (_pending.Count > 0)
        {
            _pending.Sort((a, b) => a.StartQpc.CompareTo(b.StartQpc));
            foreach (var g in _pending)
            {
                double t0 = Clock.ToT(g.StartQpc), t1 = Clock.ToT(g.EndQpc), ms = g.Ms;
                _csv.Row(t0, ms, g.Core);
                for (int b = Buckets.Length - 1; b >= 0; b--)
                    if (ms >= Buckets[b]) { for (int k = 0; k <= b; k++) Interlocked.Increment(ref Hist[g.Core, k]); break; }
                if (ms > MaxGap5s) MaxGap5s = ms;

                if (_cur != null && t0 <= _cur.T1 + _tolMs / 1000.0)
                {
                    _cur.T1 = Math.Max(_cur.T1, t1);
                    _cur.MaxMs = Math.Max(_cur.MaxMs, ms);
                    _cur.Cores.Add(g.Core);
                    _cur.Gaps++;
                }
                else
                {
                    if (_cur != null) ClusterReady?.Invoke(_cur);
                    _cur = new StallCluster { T0 = t0, T1 = t1, MaxMs = ms, Gaps = 1 };
                    _cur.Cores.Add(g.Core);
                }
            }
            _pending.Clear();
        }
        if (_cur != null && _cur.T1 < now - 0.15) { ClusterReady?.Invoke(_cur); _cur = null; }
    }

    public void Flush() => _csv.Flush();
}

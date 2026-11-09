using System.Diagnostics;

namespace VrHitchLog;

/// <summary>
/// Keeps Windows Performance Recorder running in its in-memory circular buffer and, on a big hitch,
/// saves the last ~minute of ETW data to an .etl file (for Windows Performance Analyzer).
/// </summary>
internal sealed class Wpr
{
    private readonly string _dir;
    private readonly int _max;
    private readonly double _minGapS;
    private int _captures;
    private double _lastCaptureT = -1e9;
    private volatile bool _busy;
    private bool _running;
    public bool Enabled { get; private set; }
    public bool Busy => _busy;
    public int Captures => _captures;
    public double BusyUntil { get; private set; } = -1;

    private static readonly string[][] ProfileSets =
    {
        new[] { "GeneralProfile", "CPU", "DiskIO", "GPU" },
        new[] { "GeneralProfile", "CPU", "DiskIO" },
        new[] { "GeneralProfile", "CPU" },
    };

    public Wpr(string dir, int max, double minGapS) { _dir = dir; _max = max; _minGapS = minGapS; }

    public bool Start()
    {
        Run("-cancel", out _); // stop a recording left over from earlier
        foreach (var set in ProfileSets)
        {
            string args = string.Join(" ", set.Select(p => "-start " + p));
            if (Run(args, out string output) == 0)
            {
                Log.Info($"WPR запущен ({string.Join(", ", set)}), кольцевой буфер в памяти.");
                _running = Enabled = true;
                return true;
            }
            Log.File($"wpr {args} failed: {output}");
        }
        Log.Info("WPR не запустился (нужны права администратора). Продолжаю без трасс.");
        return false;
    }

    /// <summary>Requests a capture of the event at session time t. The trace is saved a few seconds later.</summary>
    public string? Request(double t, string reason)
    {
        if (!Enabled || _busy || _captures >= _max || t - _lastCaptureT < _minGapS) return null;
        _busy = true;
        _captures++;
        _lastCaptureT = t;
        string file = Path.Combine(_dir, $"hitch-{_captures:00}-{Clock.StartWall.AddSeconds(t):HHmmss}.etl");
        BusyUntil = double.MaxValue;
        new Thread(() =>
        {
            Thread.Sleep(3000); // keep a bit of the aftermath
            Log.Info($"WPR: сохраняю трассу ({reason}) → {Path.GetFileName(file)}. Пара минут нагрузки на ПК — это нормально.");
            int rc = Run($"-stop \"{file}\" \"{reason}\"", out string output);
            if (rc != 0) Log.Info("WPR: сохранить не удалось: " + output.Trim());
            else Log.Info("WPR: трасса сохранена.");
            _running = false;
            if (_captures < _max && Start()) { }
            else Enabled = false;
            BusyUntil = Clock.Now + 2; // let the system settle before counting events again
            _busy = false;
        }) { IsBackground = true, Name = "wpr" }.Start();
        return Path.GetFileName(file);
    }

    public void Stop()
    {
        if (_running && !_busy) { Run("-cancel", out _); _running = false; }
    }

    private static int Run(string args, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo("wpr.exe", args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            output = o;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}

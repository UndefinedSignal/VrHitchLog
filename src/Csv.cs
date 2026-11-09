using System.Globalization;
using System.Text;

namespace VrHitchLog;

/// <summary>Thread-safe CSV file writer. Rows are flushed by <see cref="Flush"/> (called periodically from the main loop).</summary>
internal sealed class Csv : IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _lock = new();
    private readonly StringBuilder _sb = new();
    private bool _closed;
    public long Rows { get; private set; }

    private static readonly List<Csv> All = new();

    public Csv(string path, string header)
    {
        _w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
        _w.WriteLine(header);
        lock (All) All.Add(this);
    }

    /// <summary>Flushes and closes every CSV file, so the session folder can be zipped.</summary>
    public static void CloseAll()
    {
        Csv[] all;
        lock (All) all = All.ToArray();
        foreach (var c in all) c.Dispose();
    }

    public void Row(params object?[] values)
    {
        lock (_lock)
        {
            if (_closed) return;
            _sb.Clear();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) _sb.Append(',');
                _sb.Append(Format(values[i]));
            }
            _w.WriteLine(_sb.ToString());
            Rows++;
        }
    }

    public static string Format(object? v) => v switch
    {
        null => "",
        double d => double.IsNaN(d) ? "" : d.ToString("0.####", CultureInfo.InvariantCulture),
        float f => float.IsNaN(f) ? "" : f.ToString("0.###", CultureInfo.InvariantCulture),
        string s => Quote(s),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(v.ToString() ?? ""),
    };

    private static string Quote(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Flush() { lock (_lock) if (!_closed) _w.Flush(); }
    public void Dispose() { lock (_lock) { if (_closed) return; _closed = true; _w.Dispose(); } }
}

internal static class Log
{
    private static StreamWriter? _file;
    private static readonly object Lock = new();

    public static void Open(string path) => _file = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };

    public static void Info(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        lock (Lock)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    /// <summary>Only to the log file (not the console).</summary>
    public static void File(string msg)
    {
        lock (Lock) _file?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }

    public static void Close() { lock (Lock) { _file?.Dispose(); _file = null; } }
}

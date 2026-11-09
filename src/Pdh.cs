using System.Runtime.InteropServices;
using static VrHitchLog.Native;

namespace VrHitchLog;

/// <summary>Minimal PDH wrapper. Counters are added by their English names, so a localized Windows works too.</summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _q;
    private readonly List<string> _failed = new();
    public IReadOnlyList<string> Failed => _failed;

    public PdhQuery()
    {
        uint st = PdhOpenQueryW(null, IntPtr.Zero, out _q);
        if (st != 0) throw new InvalidOperationException($"PdhOpenQuery failed 0x{st:X8}");
    }

    /// <summary>Returns null if the counter does not exist on this machine.</summary>
    public PdhCounter? Add(string path)
    {
        uint st = PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out IntPtr h);
        if (st != 0) { _failed.Add($"{path} (0x{st:X8})"); return null; }
        return new PdhCounter(h, path);
    }

    public bool Collect() => PdhCollectQueryData(_q) == 0;

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; }
    }
}

internal sealed class PdhCounter
{
    private readonly IntPtr _h;
    public string Path { get; }
    private IntPtr _buf = IntPtr.Zero;
    private uint _bufSize;

    public PdhCounter(IntPtr h, string path) { _h = h; Path = path; }

    /// <summary>Single value, NaN if not available yet (rate counters need two collections).</summary>
    public double Value()
    {
        uint st = PdhGetFormattedCounterValue(_h, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v);
        if (st != 0 || v.CStatus > 1) return double.NaN;
        return v.DoubleValue;
    }

    /// <summary>All instances of a wildcard counter. Instances that appear later are picked up automatically.</summary>
    public List<(string Name, double Value)> Array()
    {
        var result = new List<(string, double)>();
        uint size = _bufSize;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint st = PdhGetFormattedCounterArrayW(_h, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, _buf);
            if (st == PDH_MORE_DATA)
            {
                if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                size = Math.Max(size, 1024) * 2;
                _buf = Marshal.AllocHGlobal((int)size);
                _bufSize = size;
                continue;
            }
            if (st != 0 || _buf == IntPtr.Zero) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W on x64: LPWSTR szName (8) + PDH_FMT_COUNTERVALUE (16) = 24 bytes
            const int itemSize = 24;
            for (int i = 0; i < count; i++)
            {
                IntPtr p = _buf + i * itemSize;
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p)) ?? "";
                uint cstatus = (uint)Marshal.ReadInt32(p, 8);
                double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 16));
                if (cstatus <= 1) result.Add((name, val));
            }
            return result;
        }
        return result;
    }
}

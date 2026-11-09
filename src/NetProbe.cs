using System.Net.NetworkInformation;

namespace VrHitchLog;

/// <summary>
/// Pings the headset 50 times a second (ICMP) and writes every round trip to net.csv.
/// A stall in the Wi-Fi link or on the headset shows up as a burst of late or lost replies.
/// </summary>
internal sealed class NetProbe
{
    private readonly string _host;
    private readonly Csv _csv;
    private readonly object _lock = new();
    public readonly List<(double T, double Rtt)> Samples = new(); // Rtt = -1 for a lost reply
    public int Sent, Lost;
    public double WorstLast5s;

    public NetProbe(string dir, string host)
    {
        _host = host;
        _csv = new Csv(Path.Combine(dir, "net.csv"), "t_send,rtt_ms");
    }

    public string Host => _host;
    public void Flush() => _csv.Flush();

    public void Run(CancellationToken ct)
    {
        byte[] payload = new byte[32];
        long next = System.Diagnostics.Stopwatch.GetTimestamp();
        long period = Clock.Freq / 50;
        while (!ct.IsCancellationRequested)
        {
            next += period;
            long wait = (next - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 / Clock.Freq;
            if (wait > 0) ct.WaitHandle.WaitOne((int)wait);
            if (ct.IsCancellationRequested) break;

            double tSend = Clock.Now;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var ping = new Ping(); // one outstanding request per Ping instance
            Interlocked.Increment(ref Sent);
            ping.SendPingAsync(_host, 1000, payload).ContinueWith(task =>
            {
                double rtt = -1;
                if (task.IsCompletedSuccessfully && task.Result.Status == IPStatus.Success)
                    rtt = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / Clock.Freq;
                else Interlocked.Increment(ref Lost);
                _csv.Row(tSend, rtt);
                lock (_lock)
                {
                    Samples.Add((tSend, rtt));
                    double w = rtt < 0 ? 1000 : rtt;
                    if (w > WorstLast5s) WorstLast5s = w;
                }
                ping.Dispose();
            });
        }
    }

    public List<(double T, double Rtt)> Snapshot() { lock (_lock) return Samples.ToList(); }
}

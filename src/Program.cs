using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace VrHitchLog;

internal static class Program
{
    private sealed class Options
    {
        public string OutRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrHitchLog");
        public double Minutes;        // 0 = until stopped
        public double GapMs = 4;      // per-core gaps written to stalls.csv
        public double EventMs = 20;   // stall / frame spike counted as an event
        public bool Wpr;
        public double WprMs = 40;
        public int WprMax = 3;
        public int[]? Cores;
        public bool NoProbe;
        public string? PingHost;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Options o;
        try { o = Parse(args); }
        catch (Exception ex) { Console.WriteLine(ex.Message); PrintHelp(); return 2; }

        DisableQuickEdit();
        string dir = Path.Combine(o.OutRoot, "session-" + Clock.StartWall.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        Log.Open(Path.Combine(dir, "log.txt"));
        Log.Info($"VrHitchLog 0.3.1 — пишу в {dir}");

        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
        Native.timeBeginPeriod(1);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (o.Minutes > 0) cts.CancelAfter(TimeSpan.FromMinutes(o.Minutes));

        var procs = new ProcessSampler(dir);
        string selfCheck = procs.SelfCheck();
        var sys = new SystemSampler(dir, procs);
        bool admin = IsAdmin();
        Wpr? wpr = null;
        if (o.Wpr)
        {
            if (!admin) Log.Info("--wpr требует запуска от администратора — трассы WPR выключены.");
            else { wpr = new Wpr(dir, o.WprMax, 120); if (!wpr.Start()) wpr = null; }
        }
        var probe = new StallProbe(dir, o.GapMs, o.NoProbe ? Array.Empty<int>() : o.Cores);
        var vr = new SteamVrSampler(dir, o.EventMs);
        var net = o.PingHost != null ? new NetProbe(dir, o.PingHost) : null;
        var ev = new EventDetector(dir, o.EventMs, o.WprMs, Math.Max(1, probe.ProbeCount), sys, procs, wpr);
        probe.ClusterReady += ev.OnCluster;
        vr.FrameEventReady += ev.OnFrame;

        WriteSessionInfo(dir, args, o, admin, selfCheck, procs);

        var threads = new List<Thread>
        {
            new(() => sys.Run(cts.Token)) { IsBackground = true, Name = "sys", Priority = ThreadPriority.AboveNormal },
            new(() => vr.Run(cts.Token)) { IsBackground = true, Name = "vr", Priority = ThreadPriority.AboveNormal },
        };
        if (net != null)
        {
            threads.Add(new(() => net.Run(cts.Token)) { IsBackground = true, Name = "net", Priority = ThreadPriority.AboveNormal });
            Log.Info($"Пингую шлем {net.Host} 50 раз в секунду (net.csv).");
        }
        if (procs.Ok) threads.Add(new(() => procs.Run(cts.Token)) { IsBackground = true, Name = "procs" });
        else Log.Info("Сбор по процессам выключен: проверка структуры не пройдена (" + selfCheck + ").");
        foreach (var t in threads) t.Start();
        probe.Start(cts.Token);

        Log.Info($"Идёт сбор: {probe.ProbeCount} потоков-зондов, счётчики системы, процессы, кадры SteamVR. " +
                 (o.Minutes > 0 ? $"Остановится через {o.Minutes} мин. " : "") + "Остановить: Ctrl+C или Q.");

        var sw = Stopwatch.StartNew();
        long lastFlush = 0, lastStatus = 0;
        while (!cts.IsCancellationRequested)
        {
            cts.Token.WaitHandle.WaitOne(100);
            try
            {
                probe.Drain();
                ev.Tick();
                if (KeyQ()) cts.Cancel();
                if (sw.ElapsedMilliseconds - lastFlush >= 2000)
                {
                    lastFlush = sw.ElapsedMilliseconds;
                    probe.Flush(); sys.Flush(); procs.Flush(); vr.Flush(); ev.Flush(); net?.Flush();
                }
                if (sw.ElapsedMilliseconds - lastStatus >= 5000)
                {
                    lastStatus = sw.ElapsedMilliseconds;
                    Status(vr, probe, sys, ev, wpr, net);
                }
            }
            catch (Exception ex) { Log.File("main loop: " + ex); }
        }

        Log.Info("Останавливаюсь...");
        foreach (var t in threads) t.Join(3000);
        probe.Drain();
        ev.Tick(final: true);
        wpr?.Stop();
        Native.timeEndPeriod(1);

        double duration = Clock.Now;
        string summary = Summary.Build(duration, vr, probe, ev, wpr, o.EventMs, net);
        File.WriteAllText(Path.Combine(dir, "summary.txt"), summary, new UTF8Encoding(false));
        probe.Flush(); sys.Flush(); procs.Flush(); vr.Flush(); ev.Flush(); net?.Flush();
        Console.WriteLine();
        Console.WriteLine(summary);
        Csv.CloseAll();
        Log.Close();

        string zip = dir + ".zip";
        try
        {
            if (File.Exists(zip)) File.Delete(zip);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)))
                    z.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
            Console.WriteLine($"Готово. Архив для отправки: {zip}");
            if (Directory.GetFiles(dir, "*.etl").Length > 0)
                Console.WriteLine($"Трассы WPR (.etl) лежат отдельно в {dir} — их открывают в Windows Performance Analyzer.");
        }
        catch (Exception ex) { Console.WriteLine("Не удалось упаковать архив: " + ex.Message + $". Данные в {dir}"); }
        return 0;
    }

    private static void Status(SteamVrSampler vr, StallProbe probe, SystemSampler sys, EventDetector ev, Wpr? wpr, NetProbe? net)
    {
        var ci = CultureInfo.InvariantCulture;
        string vrs = vr.Connected ? $"SteamVR {vr.FramesLast5s / 5.0:0} кадр/с, фризов ≥60 мс: {vr.BigGapsLast5s}" : "SteamVR —";
        string line = $"{TimeSpan.FromSeconds(Clock.Now):hh\\:mm\\:ss} | {vrs} | за 5 с: остановок {ev.StallEvents5s}, спайков кадра {ev.FrameEvents5s}" +
                      $" | макс. остановка ядра {probe.MaxGap5s.ToString("0.0", ci)} мс | DPC на ядре до {sys.WorstDpcCoreMax5s.ToString("0.0", ci)}%" +
                      (net != null ? $" | пинг шлема до {(net.WorstLast5s >= 1000 ? "потерь" : net.WorstLast5s.ToString("0", ci) + " мс")}" : "") +
                      (wpr != null ? $" | WPR {wpr.Captures}{(wpr.Busy ? " (сохраняю)" : "")}" : "");
        Log.Info(line);
        vr.FramesLast5s = 0; vr.BigGapsLast5s = 0; ev.StallEvents5s = 0; ev.FrameEvents5s = 0; probe.MaxGap5s = 0; sys.WorstDpcCoreMax5s = 0;
        if (net != null) net.WorstLast5s = 0;
    }

    private static bool KeyQ()
    {
        try
        {
            while (Console.KeyAvailable)
                if (Console.ReadKey(true).Key == ConsoleKey.Q) return true;
        }
        catch (InvalidOperationException) { }
        return false;
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"после {a} нужно значение");
            double D() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--out": o.OutRoot = Next(); break;
                case "--minutes": o.Minutes = D(); break;
                case "--gap-ms": o.GapMs = D(); break;
                case "--event-ms": o.EventMs = D(); break;
                case "--wpr": o.Wpr = true; break;
                case "--wpr-ms": o.WprMs = D(); break;
                case "--wpr-max": o.WprMax = (int)D(); break;
                case "--cores": o.Cores = Next().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); break;
                case "--no-probe": o.NoProbe = true; break;
                case "--ping": o.PingHost = Next(); break;
                case "-h": case "--help": case "/?": PrintHelp(); Environment.Exit(0); break;
                default: throw new ArgumentException("неизвестный параметр: " + args[i]);
            }
        }
        return o;
    }

    private static void PrintHelp() => Console.WriteLine("""
        VrHitchLog — запись данных для поиска микрофризов в VR.

          --minutes N     остановиться через N минут (по умолчанию — до Ctrl+C или Q)
          --wpr           держать WPR в памяти и сохранять трассу при сильном фризе (нужен администратор)
          --wpr-ms N      порог остановки для трассы WPR, мс (40; для спайка кадра — вдвое больше)
          --wpr-max N     сколько трасс сохранить максимум (3)
          --event-ms N    что считать событием: остановка ядра / интервал кадра, мс (20)
          --gap-ms N      писать в stalls.csv остановки ядра от N мс (4)
          --cores 0,2,4   зонды только на этих ядрах (по умолчанию — на всех)
          --no-probe      без зондов
          --ping IP       пинговать шлем 50 раз в секунду и сопоставить задержки сети с фризами
          --out DIR       куда писать (по умолчанию %LOCALAPPDATA%\VrHitchLog)
        """);

    private static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    /// <summary>A click in the console window (QuickEdit) would freeze console output and with it our threads.</summary>
    private static void DisableQuickEdit()
    {
        IntPtr h = GetStdHandle(-10);
        if (GetConsoleMode(h, out uint mode)) SetConsoleMode(h, (mode & ~0x0040u) | 0x0080u);
    }

    private static void WriteSessionInfo(string dir, string[] args, Options o, bool admin, string selfCheck, ProcessSampler procs)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.AppendLine(s);
        L($"VrHitchLog 0.3.1, start {Clock.StartWall:yyyy-MM-dd HH:mm:ss}");
        L("args: " + string.Join(' ', args));
        L($"thresholds: gap {o.GapMs} ms, event {o.EventMs} ms, wpr {(o.Wpr ? $"on, {o.WprMs} ms, max {o.WprMax}" : "off")}");
        L($"admin: {admin}");
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            L($"OS: {k?.GetValue("ProductName")} {k?.GetValue("DisplayVersion")} build {k?.GetValue("CurrentBuild")}.{k?.GetValue("UBR")}");
            using var c = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            L($"CPU: {c?.GetValue("ProcessorNameString")?.ToString()?.Trim()}, logical {Environment.ProcessorCount}");
            using var b = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            L($"Board: {b?.GetValue("BaseBoardManufacturer")} {b?.GetValue("BaseBoardProduct")}, BIOS {b?.GetValue("BIOSVersion")} {b?.GetValue("BIOSReleaseDate")}");
        }
        catch (Exception ex) { L("registry: " + ex.Message); }
        L($"RAM: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0.0} GB");
        if (X86Base.IsSupported)
        {
            var (_, _, ecx, _) = X86Base.CpuId(1, 0);
            L($"hypervisor present (Hyper-V/WSL2/VBS): {((ecx >> 31) & 1) == 1}");
        }
        if (Native.NtQueryTimerResolution(out uint min, out uint max, out uint cur) == 0)
            L($"timer resolution: current {cur / 10000.0:0.###} ms (coarsest {min / 10000.0:0.###}, finest {max / 10000.0:0.###})");
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/getactivescheme") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding((int)GetOEMCP()) };
            using var p = Process.Start(psi)!;
            L("power: " + p.StandardOutput.ReadToEnd().Trim());
            p.WaitForExit();
        }
        catch (Exception ex) { L("power: " + ex.Message); }
        L($"process sampler self-check: {selfCheck}");
        L("");
        L("running processes at start (names only):");
        try
        {
            var names = Process.GetProcesses().Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            L(string.Join(", ", names));
        }
        catch (Exception ex) { L(ex.Message); }
        File.WriteAllText(Path.Combine(dir, "session.txt"), sb.ToString(), new UTF8Encoding(false));
    }
}

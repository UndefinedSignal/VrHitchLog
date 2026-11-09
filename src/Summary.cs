using System.Globalization;
using System.Text;

namespace VrHitchLog;

internal static class Summary
{
    public static string Build(double duration, SteamVrSampler vr, StallProbe probe, EventDetector ev, Wpr? wpr, double eventMs, NetProbe? net = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L($"VrHitchLog summary — {Clock.StartWall:yyyy-MM-dd HH:mm:ss}, длительность {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}");
        L();
        L("== SteamVR ==");
        if (vr.Frames == 0) L("кадров не получено (SteamVR не был запущен или openvr_api.dll не найден)");
        else
        {
            L($"частота {vr.RefreshHz:0.#} Гц, кадров компоситора {vr.Frames}");
            L($"кадров с пропуском (dropped>0): {vr.FramesDropped} ({100.0 * vr.FramesDropped / vr.Frames:0.###}%), всего пропущено {vr.DroppedTotal}");
            L($"кадр приложения показан повторно (presents>1, репроекция): {vr.Repeated} ({100.0 * vr.Repeated / vr.Frames:0.##}%)");
            L($"mispresented: {vr.MisPresented}; пропущено записей при опросе: {vr.MissedRecords}");
            L($"шкала времени: {vr.TimeBaseInfo}");
        }
        L();

        L($"== Остановки ядер (поток на каждом ядре, сон 1 мс) — сколько раз ядро было занято дольше N мс ==");
        L("core   " + string.Join("", StallProbe.Buckets.Select(b => $"{">=" + b,8}")));
        for (int c = 0; c < probe.CoreCount; c++)
        {
            var row = Enumerable.Range(0, StallProbe.Buckets.Length).Select(k => probe.Hist[c, k]).ToArray();
            L($"{c,4}   " + string.Join("", row.Select(v => $"{v,8}")));
        }
        L();

        var events = ev.All.Where(e => !e.DuringWpr).ToList();
        var stalls = events.Where(e => e.Kind == "stall").ToList();
        var frames = events.Where(e => e.Kind == "frame").ToList();
        L("== Заметные фризы картинки: интервал кадра компоситора ≥ 60 мс ==");
        List<(double T, double Ms)> gaps;
        lock (vr.BigGaps) gaps = vr.BigGaps.ToList();
        // skip the first 30 s after SteamVR starts (loading), merge gaps closer than 0.5 s
        double first = gaps.Count > 0 ? gaps[0].T : 0;
        var g = new List<(double T, double Ms)>();
        foreach (var x in gaps.Where(x => x.T > first + 30))
            if (g.Count > 0 && x.T - g[^1].T < 0.5) g[^1] = (g[^1].T, g[^1].Ms + x.Ms); else g.Add(x);
        if (vr.Frames == 0) L("нет данных SteamVR");
        else if (g.Count == 0) L("ни одного (не считая первых 30 с после запуска SteamVR)");
        else
        {
            double span = Math.Max(1, (vr.Frames / Math.Max(1, vr.RefreshHz)) / 60.0);
            L($"фризов: {g.Count} (~{g.Count / span:0.#} в минуту), средняя длина {g.Average(x => x.Ms):0} мс, максимум {g.Max(x => x.Ms):0} мс");
            if (g.Count >= 3)
            {
                var iv = g.Zip(g.Skip(1), (a, b) => b.T - a.T).OrderBy(x => x).ToList();
                double med = iv[iv.Count / 2];
                L($"медианный интервал между ними {med.ToString("0.00", ci)} с; в пределах ±3% от медианы: {100.0 * iv.Count(x => Math.Abs(x - med) <= med * 0.03) / iv.Count:0}% (близко к 100% = строго периодично)");
            }
        }
        L();

        if (net != null)
        {
            var pings = net.Snapshot().OrderBy(x => x.T).ToList();
            var ok = pings.Where(x => x.Rtt >= 0).Select(x => x.Rtt).OrderBy(x => x).ToList();
            L($"== Пинг шлема {net.Host} (50 раз/с) ==");
            if (ok.Count == 0) L("ответов нет: шлем не отвечает на ping или неверный адрес");
            else
            {
                double P(double q) => ok[Math.Min(ok.Count - 1, (int)(q * ok.Count))];
                L($"отправлено {net.Sent}, потеряно {net.Lost}; задержка: медиана {P(0.5):0.0} мс, 99% {P(0.99):0.0} мс, макс {ok[^1]:0} мс");
                var spikes = new List<double>();
                foreach (var x in pings.Where(x => x.Rtt < 0 || x.Rtt >= 50))
                    if (spikes.Count == 0 || x.T - spikes[^1] > 0.5) spikes.Add(x.T);
                L($"всплесков задержки ≥ 50 мс или потерь: {spikes.Count}");
                if (spikes.Count >= 3)
                {
                    var iv = spikes.Zip(spikes.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
                    L($"медианный интервал между всплесками {iv[iv.Count / 2].ToString("0.00", ci)} с");
                }
                if (g.Count > 0)
                {
                    // for every visible freeze: the worst ping sent during it (or just before)
                    int hit = 0;
                    var worst = new List<double>();
                    foreach (var fz in g)
                    {
                        double t1 = fz.T, t0 = fz.T - fz.Ms / 1000.0 - 0.15;
                        var w = pings.Where(x => x.T >= t0 && x.T <= t1).Select(x => x.Rtt < 0 ? 1000 : x.Rtt).DefaultIfEmpty(double.NaN).Max();
                        if (!double.IsNaN(w)) { worst.Add(w); if (w >= 50) hit++; }
                    }
                    if (worst.Count > 0)
                        L($"фризов, во время которых пинг шлема вырос до ≥ 50 мс или терялся: {hit} из {worst.Count} " +
                          $"(медиана худшего пинга во время фриза {worst.OrderBy(x => x).ElementAt(worst.Count / 2):0} мс). " +
                          "Почти все — значит, стоит сеть или шлем; почти ни одного — проблема на ПК.");
                }
            }
            L();
        }

        L($"== События (порог {eventMs} мс; без интервалов записи WPR) ==");
        L($"остановок ядер: {stalls.Count}, из них почти все ядра сразу (≥75%): {stalls.Count(e => e.AllCores)}");
        L($"спайков кадра SteamVR: {frames.Count}");
        if (frames.Count > 0)
        {
            int withStall = frames.Count(e => e.OverlapMs >= 8);
            L($"  совпали с остановкой ядра ≥8 мс: {withStall} ({100.0 * withStall / frames.Count:0}%)");
            L($"  без остановки ядер: {frames.Count - withStall} — значит, причина не в драйверах/прошивке, а выше (приложение, GPU, SteamVR, стриминг, память)");
        }
        if (wpr != null) L($"трасс WPR сохранено: {wpr.Captures}");
        L();

        // periodicity: merge events closer than 1 s
        var times = events.Select(e => e.T0).OrderBy(t => t).ToList();
        var merged = new List<double>();
        foreach (var t in times) if (merged.Count == 0 || t - merged[^1] > 1.0) merged.Add(t);
        L("== Периодичность (события ближе 1 с объединены) ==");
        if (merged.Count < 3) L("слишком мало событий");
        else
        {
            var iv = merged.Zip(merged.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
            double med = iv[iv.Count / 2];
            int nearMed = iv.Count(x => Math.Abs(x - med) <= Math.Max(1, med * 0.1));
            L($"групп событий: {merged.Count}, медианный интервал {med.ToString("0.0", ci)} с, в пределах ±10% от медианы: {100.0 * nearMed / iv.Count:0}%");
            L("частые интервалы (округл. до 1 с): " + string.Join(", ",
                iv.GroupBy(x => Math.Round(x)).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key}с×{g.Count()}")));
        }
        L();

        L("== Худшие события ==");
        L("время          тип    мс      ядер  перекрытие_мс");
        foreach (var e in events.OrderByDescending(e => e.DurMs).Take(30))
            L($"{Clock.Wall(e.T0)}  {e.Kind,-5}  {e.DurMs,6:0.0}  {(e.Kind == "stall" ? e.Cores.ToString() : "-"),4}  {e.OverlapMs,8:0.0}");
        return sb.ToString();
    }
}

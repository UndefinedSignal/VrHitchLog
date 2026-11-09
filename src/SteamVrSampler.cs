using System.Runtime.InteropServices;
using Valve.VR;

namespace VrHitchLog;

internal sealed record FrameEvent(double T, double IntervalMs, uint FrameIndex, uint Dropped, uint MisPresented, uint Presents, float ClientIntervalMs);

/// <summary>
/// Connects to SteamVR as a background app (does not start SteamVR, does not keep it running) and reads the
/// compositor's per-frame timing — the same data fpsVR shows.
/// </summary>
internal sealed class SteamVrSampler
{
    private readonly Csv _csv;
    private readonly double _eventMs;
    public bool Connected { get; private set; }
    public float RefreshHz { get; private set; }
    public long Frames, FramesDropped, DroppedTotal, MisPresented, Repeated, MissedRecords;
    /// <summary>Compositor frame intervals of 60 ms and more (the freezes you can see), session time and length.</summary>
    public readonly List<(double T, double Ms)> BigGaps = new();
    public int BigGapsLast5s;
    public int FramesLast5s;
    public event Action<FrameEvent>? FrameEventReady;
    public string TimeBaseInfo { get; private set; } = "";

    private uint _lastIndex;
    private bool _haveLast;
    private double _lastSys;
    private double _offset = double.NaN;   // QPC seconds - SteamVR system seconds
    private bool _exact;                   // SteamVR time is the same QPC clock

    public SteamVrSampler(string dir, double eventMs)
    {
        _eventMs = eventMs;
        _csv = new Csv(Path.Combine(dir, "frames.csv"),
            "t,frame,sys_s,interval_ms,presents,mispresented,dropped,reproj_flags,client_interval_ms," +
            "pre_submit_gpu_ms,post_submit_gpu_ms,total_render_gpu_ms,comp_render_gpu_ms,comp_render_cpu_ms,comp_idle_cpu_ms," +
            "present_call_cpu_ms,wait_for_present_cpu_ms,submit_frame_ms,wait_get_poses_ms,new_poses_ready_ms,new_frame_ready_ms," +
            "comp_update_start_ms,comp_update_end_ms,comp_render_start_ms,vsyncs_ready,vsyncs_to_first_view");
    }

    public void Flush() => _csv.Flush();

    public void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Connected)
            {
                TryConnect();
                if (!Connected) { ct.WaitHandle.WaitOne(3000); continue; }
            }
            try
            {
                if (!PumpEvents()) { Disconnect("SteamVR закрывается"); continue; }
                Collect();
            }
            catch (Exception ex)
            {
                Log.Info("SteamVR: ошибка чтения, переподключение: " + ex.Message);
                Disconnect(null);
            }
            ct.WaitHandle.WaitOne(50);
        }
        if (Connected) Disconnect(null);
    }

    private bool _loggedWait;

    private void TryConnect()
    {
        EVRInitError err = EVRInitError.None;
        try
        {
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
        }
        catch (DllNotFoundException)
        {
            if (!_loggedWait) Log.Info("openvr_api.dll не найден рядом с exe — кадры SteamVR писаться не будут.");
            _loggedWait = true;
            return;
        }
        if (err != EVRInitError.None)
        {
            if (!_loggedWait) Log.Info(err == EVRInitError.Init_NoServerForBackgroundApp
                ? "SteamVR не запущен — жду (остальной сбор уже идёт)."
                : $"SteamVR: не подключиться ({err}) — повторяю раз в 3 с.");
            _loggedWait = true;
            return;
        }
        if (OpenVR.Compositor == null)
        {
            // Some runtimes don't hand the compositor to background apps; reconnect as an overlay (SteamVR is already running).
            OpenVR.Shutdown();
            err = EVRInitError.None;
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay);
            if (err != EVRInitError.None || OpenVR.Compositor == null)
            {
                if (err == EVRInitError.None) OpenVR.Shutdown();
                Log.Info($"SteamVR: compositor недоступен ({err}).");
                return;
            }
        }
        Connected = true;
        _loggedWait = false;
        _haveLast = false;
        var perr = ETrackedPropertyError.TrackedProp_Success;
        RefreshHz = OpenVR.System.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref perr);
        Log.Info($"SteamVR подключён, частота шлема {RefreshHz:0.#} Гц.");
    }

    private void Disconnect(string? why)
    {
        try { OpenVR.Shutdown(); } catch { }
        Connected = false;
        if (why != null) Log.Info("SteamVR: " + why + ", жду следующего запуска.");
    }

    /// <returns>false if SteamVR asked us to quit.</returns>
    private bool PumpEvents()
    {
        var ev = new VREvent_t();
        uint size = (uint)Marshal.SizeOf<VREvent_t>();
        while (OpenVR.System.PollNextEvent(ref ev, size))
        {
            if (ev.eventType == (uint)EVREventType.VREvent_Quit)
            {
                OpenVR.System.AcknowledgeQuit_Exiting();
                return false;
            }
        }
        return true;
    }

    private readonly List<Compositor_FrameTiming> _batch = new();

    private void Collect()
    {
        var comp = OpenVR.Compositor;
        uint size = (uint)Marshal.SizeOf<Compositor_FrameTiming>();
        _batch.Clear();
        // framesAgo = 0 is the frame in progress; start from the last completed one
        for (uint ago = 1; ago < 200; ago++)
        {
            var ft = new Compositor_FrameTiming { m_nSize = size };
            if (!comp.GetFrameTiming(ref ft, ago)) break;
            if (ft.m_nFrameIndex == 0) break;
            if (_haveLast && ft.m_nFrameIndex <= _lastIndex) break;
            _batch.Add(ft);
            if (!_haveLast && ago >= 2) break; // on connect, start from "now"
        }
        if (_batch.Count == 0) return;
        double pollQpc = Clock.QpcSeconds;

        // Time base: estimate QPC - SteamVR system time from the newest completed frame (min over all polls).
        double est = pollQpc - _batch[0].m_flSystemTimeInSeconds;
        if (double.IsNaN(_offset) || est < _offset) _offset = est;
        if (!_exact && Math.Abs(_offset) < 0.05) { _exact = true; _offset = 0; }
        TimeBaseInfo = _exact ? "SteamVR system time == QPC (exact)" : $"estimated offset {_offset:0.000} s";

        for (int i = _batch.Count - 1; i >= 0; i--)
        {
            var f = _batch[i];
            if (_haveLast && f.m_nFrameIndex > _lastIndex + 1) MissedRecords += f.m_nFrameIndex - _lastIndex - 1;
            double interval = (_haveLast && f.m_nFrameIndex == _lastIndex + 1) ? (f.m_flSystemTimeInSeconds - _lastSys) * 1000.0 : double.NaN;
            double t = f.m_flSystemTimeInSeconds + (_exact ? 0 : _offset) - Clock.StartQpcSeconds;

            _csv.Row(t, f.m_nFrameIndex, f.m_flSystemTimeInSeconds, interval, f.m_nNumFramePresents, f.m_nNumMisPresented,
                f.m_nNumDroppedFrames, f.m_nReprojectionFlags, f.m_flClientFrameIntervalMs,
                f.m_flPreSubmitGpuMs, f.m_flPostSubmitGpuMs, f.m_flTotalRenderGpuMs, f.m_flCompositorRenderGpuMs,
                f.m_flCompositorRenderCpuMs, f.m_flCompositorIdleCpuMs, f.m_flPresentCallCpuMs, f.m_flWaitForPresentCpuMs,
                f.m_flSubmitFrameMs, f.m_flWaitGetPosesCalledMs, f.m_flNewPosesReadyMs, f.m_flNewFrameReadyMs,
                f.m_flCompositorUpdateStartMs, f.m_flCompositorUpdateEndMs, f.m_flCompositorRenderStartMs,
                f.m_nNumVSyncsReadyForUse, f.m_nNumVSyncsToFirstView);

            Frames++; FramesLast5s++;
            if (f.m_nNumDroppedFrames > 0) { FramesDropped++; DroppedTotal += f.m_nNumDroppedFrames; }
            MisPresented += f.m_nNumMisPresented;
            if (f.m_nNumFramePresents > 1) Repeated++;

            double expected = RefreshHz > 1 ? 1000.0 / RefreshHz : 11.1;
            bool spike = (!double.IsNaN(interval) && interval >= Math.Max(_eventMs, expected * 2.0))
                         || f.m_nNumDroppedFrames >= 2
                         || f.m_flClientFrameIntervalMs >= Math.Max(_eventMs, expected * 3.0);
            if (!double.IsNaN(interval) && interval >= 60)
                lock (BigGaps) { BigGaps.Add((t, interval)); BigGapsLast5s++; }
            if (spike)
                FrameEventReady?.Invoke(new FrameEvent(t, double.IsNaN(interval) ? 0 : interval, f.m_nFrameIndex,
                    f.m_nNumDroppedFrames, f.m_nNumMisPresented, f.m_nNumFramePresents, f.m_flClientFrameIntervalMs));

            _lastIndex = f.m_nFrameIndex;
            _lastSys = f.m_flSystemTimeInSeconds;
            _haveLast = true;
        }
    }
}

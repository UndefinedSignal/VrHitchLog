# VrHitchLog

*[Русская версия](README.ru.md)*

A Windows console tool for tracking down **periodic micro-stutters in PC VR**: SteamVR, VRChat, Virtual Desktop, Pico Connect, Air Link, Steam Link. It puts on one timeline everything that can make the picture freeze for a moment:

- every SteamVR compositor frame;
- per-core CPU stalls (DPC/ISR, drivers, firmware);
- system counters and the busiest processes;
- a **50 Hz ping of the headset or router**.

At the end it tells you whether the freezes come from the PC, the game, or the network.

> **Found here:** a freeze every **~5.2 seconds** in wireless VR caused by the **Xiaomi/Redmi router service `trafficd`**. If you have a Xiaomi or Redmi router and a stutter with a fixed ~5 s rhythm, read the **[case study](docs/case-xiaomi-trafficd.md)** first.

## What it records

| File | Contents | Rate |
|---|---|---|
| `frames.csv` | every SteamVR compositor frame: interval, dropped/repeated frames, CPU/GPU time (the same data fpsVR shows) | every frame |
| `stalls.csv` | per-core stalls: a probe thread on each logical core sleeps 1 ms; if it wakes up later than 4 ms, the core was held by a driver (DPC/ISR), a realtime thread or firmware | when they happen |
| `sys.csv` | CPU, DPC and interrupt time (total and worst core), paging, memory, the slowest disk | 4/s |
| `disks.csv` | latency, queue length and throughput per disk | 4/s |
| `sys_slow.csv` | GPU engines (3D, copy, video encode…), top 3D process, GPU memory, network, temperature | 1/s |
| `procs.csv` | the busiest processes: CPU, hard page faults, disk I/O, memory | 1/s |
| `events.csv` | detected hitches with context: frame spike vs. core stalls, DPC, disk, memory, active processes | when they happen |
| `net.csv` | with `--ping IP`: send time and RTT of every ping (−1 = lost) | 50/s |
| `summary.txt` | frame statistics, stalls per core, freezes ≥60 ms and their period, ping stalls and how many freezes they match | at the end |
| `session.txt` | hardware, Windows, BIOS, hypervisor, timer resolution, power plan, names of running programs | at the start |

The console output and `summary.txt` are in Russian; the CSV files are plain numbers.

## Build

Requires the .NET SDK 8 or newer (it also runs on newer runtimes). Run **`build.cmd`**; the program appears in `out\`.

On the first build `openvr_api.dll` is downloaded from Valve's official repository. If that fails, get it from <https://github.com/ValveSoftware/openvr/tree/v2.5.1/bin/win64> and put it into `third_party\openvr\`.

## Run

1. Start **`out\run.cmd`** before or after SteamVR, in any order. It attaches to SteamVR when SteamVR appears and survives restarts; it never starts SteamVR itself.
2. Play as usual. A status line is printed every 5 seconds.
3. Stop with **Q** or **Ctrl+C**. Don't close the window with the X button, or the summary and archive won't be written.
4. The session ends up in `%LOCALAPPDATA%\VrHitchLog\session-DATE-TIME\` plus a `.zip` of it.

**`out\run-wpr.cmd`** does the same plus WPR kernel traces (asks for administrator rights). Windows Performance Recorder keeps the last minute in memory and saves it to `.etl` on a big stall (up to 3 per session). Open `.etl` files in Windows Performance Analyzer.

### Options

```
--minutes N     stop after N minutes
--ping IP       ping this host 50 times/s (net.csv) and match ping stalls with freezes
--no-probe      no per-core probes (e.g. for a pure network test)
--wpr           WPR traces of big stalls (administrator)
--wpr-ms N      core stall that triggers a trace, ms (40)
--wpr-max N     max traces per session (3)
--event-ms N    what counts as an event, ms (20)
--gap-ms N      write core stalls from N ms to stalls.csv (4)
--cores 0,2,4   probes only on these cores
--out DIR       output folder
```

Examples (from the `out` folder):
```
:: headset IP, while playing
run.cmd --ping 192.168.1.50

:: the router itself, with the PC on a cable
run.cmd --no-probe --ping 192.168.1.1 --minutes 3 --out D:\vrlog\router
```
Two instances can run at once, for example one pinging the headset and one pinging the router. Give them different `--out` folders.

## Reading the results

- **Frame spike + almost all cores stalled at once:** the whole machine stopped. Look at drivers, firmware/BIOS (SMI), the hypervisor.
- **Frame spike + one or two cores stalled:** a specific driver holding DPC/ISR (often core 0). A WPR trace shows which one.
- **Frame spike, no core stalls, ping clean:** the game (asset loading, GC), paging, disk, GPU or SteamVR itself.
- **Frame spike together with a ping stall:** the network. In `net.csv` a stall usually looks like a falling staircase of RTTs, e.g. `176, 156, 136 … 17` ms: packets were queued and released together.
- **A fixed interval between events** (every 5.2 s, 30 s, 60 s…): almost always something polling on a timer. That can be a program, a driver or **the router**.

To tell the router from the headset:
1. Ping a second Wi-Fi device (a phone with the screen on) at the same time as the headset. If both stall in the same phase, the router is the cause.
2. Ping the router itself from the wired PC. If it stalls too, the router is freezing internally.

The SteamVR log line `WaitForAcquire timed out (FAILED); rendering the next frame before the driver took the sync texture` in `Steam\logs\vrcompositor.txt`, repeating at a fixed interval, is another sign that the streaming driver is starved by the network.

## Privacy

Only numbers and process names are written: no paths, command lines or window contents. Still, look through `session.txt` before posting a session publicly: it lists your hardware and the names of running programs. The pinged IP (a local address) appears in `session.txt`, `log.txt` and `summary.txt`.

## License

MIT, see [LICENSE](LICENSE). `third_party/openvr/openvr_api.cs` is Valve's OpenVR binding under BSD-3-Clause, see [`third_party/openvr/LICENSE.md`](third_party/openvr/LICENSE.md).

# Case study: VR freezes every 5.2 seconds caused by a Xiaomi / Redmi router (`trafficd`)

*[Русская версия](case-xiaomi-trafficd.ru.md)*

**Short version.** In wireless PC VR, the picture froze for 100–180 ms almost exactly every **5.18–5.20 seconds**. The PC, SteamVR, the game and the headset were all fine. The cause was the stock firmware of a **Redmi Router AX6000** (MiWiFi, firmware 1.0.67): the Xiaomi service **`/usr/sbin/trafficd`** blocks the router for ~170 ms every ~5.2 s. Stopping it removed the freezes completely.

If your router is a Xiaomi or Redmi model on stock firmware and your stutter has a fixed rhythm of about 5 seconds, this is very likely your problem too.

---

## Setup

- Headset: PICO 4 Pro streaming over Wi-Fi 6 (5 GHz, 1200 Mbps link) with **Virtual Desktop**. The same freezes happened earlier with **Pico Connect**.
- Full body tracking: Lighthouse base stations, Vive/Tundra trackers, Index controllers.
- PC: Ryzen 7 9700X, RTX 5080, Windows 10 22H2, **wired** to the router.
- Router: Redmi Router AX6000 (RB06), stock firmware 1.0.67. That is the latest firmware Xiaomi released for this model, from January 2023.

## Symptom

- A short freeze (a "micro-stutter") every few seconds, in VRChat and in the empty SteamVR home.
- fpsVR showed a lone frame-time spike, with SteamVR itself freezing too.
- In the Virtual Desktop performance overlay, **Encoding** turned yellow first, then **Networking**.

## What did *not* help

These are worth knowing, because they are the usual advice and they cost a lot of time:

- MPO off, power plan, page file, BIOS and chipset update.
- Moving Steam/SteamVR from HDD to SSD.
- Disabling SteamVR add-on drivers and overlays.
- Changing the router's 5 GHz channel (Auto → 36) and width (160 → 80 MHz). This is still a sensible setting, but it was not the cause.
- Turning MU-MIMO off. It made the headset hit the stall less often, but the router kept stalling.

## How it was found (with VrHitchLog)

1. **Frame timing.** The SteamVR compositor stalled waiting for new head poses every **5.19 s** (±3%), even with no game running.
2. **PC ruled out.** Per-core stall probes showed **no** CPU, DPC or interrupt stalls at those moments.
3. **SteamVR log.** `vrcompositor.txt` showed the same rhythm:
   ```
   WaitForAcquire timed out (FAILED); rendering the next frame before the driver took the sync texture
   ```
   Intervals between these lines: 5.22, 5.18, 5.17, 5.19 s… The streaming driver (Virtual Desktop) did not pick up frames in time.
4. **Ping the headset 50×/s** (`--ping <headset IP>`). Every 5.18 s the headset went silent for ~180 ms. Then the delayed replies arrived all at once, so the RTT values fall in a "staircase", each 20 ms (one ping interval) lower than the last:
   ```
   176, 156, 136, 116, 96, 76, 56, 36, 17 ms ... then back to ~3 ms
   ```
   This pattern means the packets were **queued** somewhere and released together, not lost.
5. **A phone on the same 5 GHz network** showed exactly the same stalls, **in the same phase** as the headset (constant 0.38 s offset over 3 minutes). Two unrelated devices cannot share a rhythm by chance, so the clock had to be in the router.
6. **Ping the router itself over the cable** (`--ping 192.168.31.1`, the default address of Xiaomi routers). It stalled too, every 5.18 s, up to 177 ms. No Wi-Fi is involved in that path, so the router itself was freezing.
7. **SSH into the router and `top -d 1`.** In one frame out of five, `trafficd` used ~4% CPU. In busybox `top` the percentage is of all 4 cores, so 4% is about 170 ms of one core. That matches the stall length.
8. **Proof.** `/etc/init.d/trafficd stop`. The router ping went from 24 spikes ≥50 ms (max 177 ms) in 2 minutes to **0 spikes, max 11 ms**. The other Wi-Fi device went from 35 spikes and 19 lost packets to 1 random spike. The freezes in VR were gone.

`trafficd` is the Xiaomi service that collects per-device traffic statistics for the web UI and the Mi Wi-Fi app. Without it you only lose those statistics.

## Fix

Getting a shell on the router: the stock firmware has no SSH. **[XMiR-Patcher](https://github.com/openwrt-xiaomi/xmir-patcher)** (open source, runs on Windows via `run.bat`) can enable SSH on your own router. Menu item `2` opened SSH on firmware 1.0.67 without a downgrade, and item `6` made SSH permanent. XMiR-Patcher sets the root password to `root`, so **change it with `passwd`** afterwards. Item `4` makes a full flash backup; do it before changing anything.

Windows' OpenSSH client needs the old key type enabled:
```
ssh -o hostkeyalgorithms=+ssh-rsa -o pubkeyacceptedalgorithms=+ssh-rsa root@192.168.31.1
```
If it complains that the host key changed (it does after every new exploit run), run `ssh-keygen -R 192.168.31.1`.

### Test first (temporary)

```
/etc/init.d/trafficd stop
ps | grep trafficd
```
This lasts until the next router reboot. Test VR now.

### Make it survive reboots

`/etc/init.d/trafficd disable` is **not enough** on this firmware. At every boot the firmware recreates the `/etc/rc.d/S77trafficd` link (you can tell because the link is dated 1970: it is created before the clock is synced). After an unnoticed router reboot the freezes came back a week later.

What does survive a reboot is the persistent `/data` partition plus a firewall "include" script, which the firmware runs at every boot:

```sh
echo '#!/bin/sh' > /data/no_trafficd.sh
echo '(sleep 90; /etc/init.d/trafficd stop; /etc/init.d/trafficd disable) >/dev/null 2>&1 &' >> /data/no_trafficd.sh
chmod +x /data/no_trafficd.sh

uci set firewall.notrafficd=include
uci set firewall.notrafficd.type='script'
uci set firewall.notrafficd.path='/data/no_trafficd.sh'
uci set firewall.notrafficd.enabled='1'
uci commit firewall
```
The script waits 90 s so that it runs after the firmware has started all its services. Check after a `reboot`, waiting at least 2 minutes:
```
uptime
ps | grep trafficd                 # only the grep line itself should be listed
uci show firewall | grep notrafficd
```

**Undo:**
```
uci delete firewall.notrafficd; uci commit firewall
rm /data/no_trafficd.sh
/etc/init.d/trafficd enable; /etc/init.d/trafficd start
```

### Alternatives

- **OpenWrt.** Redmi AX6000 is [well supported](https://openwrt.org/toh/xiaomi/redmi_ax6000), and none of the Xiaomi background services exist there. The "stock layout" images can be installed with XMiR-Patcher (item `7`) without replacing the bootloader. Not needed in this case, but it is the cleanest long-term option.
- **A separate access point only for the headset**, wired to the PC. This avoids the home router entirely.

## How to check your own setup

You need a PC **wired** to the router, and [VrHitchLog](../README.md) or any tool that can ping 50 times per second.

1. Ping the router itself for 2–3 minutes:
   ```
   VrHitchLog.exe --no-probe --ping 192.168.31.1 --minutes 3
   ```
   Spikes of 100+ ms with a **median interval of ~5.2 s** in `summary.txt` mean the router is stalling.
2. Ping the headset (`--ping <headset IP>`) while it is connected and streaming. Look for the falling-staircase RTT pattern in `net.csv`.
3. Note that a short idle test can come out clean even while `trafficd` is running. In this case the stall was strongest while a headset was connected and streaming. The reliable check is `ps | grep trafficd` on the router.

Windows' own `ping -t` sends only one ping per second, so it catches a 180 ms stall only now and then. A high-rate ping is what makes the rhythm visible.

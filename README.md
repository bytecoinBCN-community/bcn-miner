# BCN Miner for Windows

**English** · [Українська](README.uk.md) · [Español](README.es.md) · [Português](README.pt.md) · [Türkçe](README.tr.md) · [Bahasa Indonesia](README.id.md) · [Tiếng Việt](README.vi.md) · [中文](README.zh.md)

A desktop miner for Bytecoin (BCN) that mines on the CPU and on NVIDIA, AMD and Intel Arc graphics cards. It has dark and light themes and an interface in eight languages.

This is an independent Bytecoin community project, not affiliated with the Bytecoin founders or bcndev.

![Mining screen](docs/screenshots/mining-dark.png)

## Download and run

1. Download `BCNMiner-<version>-win-portable.zip` from [bytecoin.community](https://bytecoin.community/#downloads) and unpack it anywhere, a USB stick included.
2. Run `BCNMiner.exe` and press **Download engine** (on the Devices screen, or in the banner on the Mining screen). The miner downloads the official [XMRig](https://github.com/xmrig/xmrig) release from GitHub, checks its SHA-256 and, for NVIDIA cards, adds the xmrig-cuda plugin built for your driver. From the command line the same is done by `powershell -ExecutionPolicy Bypass -File .\get-engine.ps1`.

Nothing has to be installed: .NET Framework 4.8 ships with Windows 10 and 11.

Windows Defender often flags miners as "potentially unwanted software". If `xmrig.exe` disappears, add the `engine` folder to Defender's exclusions yourself.

### Portable mode

The release zip contains a `data` folder. While it exists next to `BCNMiner.exe`, settings, logs and the tuning cache are stored there, so the whole folder can be moved to another PC. Delete `data` to keep settings in `%APPDATA%\BCN Miner` instead. Settings → About shows which mode is active.

The "Start with Windows" switch writes to the registry (`HKCU\...\Run`), and only when you turn it on.

## How it works

- **Interface:** `BCNMiner.exe`, WPF on .NET Framework 4.8.
- **Engine:** XMRig with the `cn/0` algorithm, the original CryptoNight that Bytecoin v3.5 (block v4) still uses. The miner starts XMRig as a child process, writes its config and reads statistics from its local HTTP API (`127.0.0.1` only, random token). If the miner closes or crashes, the engine stops with it.
- **Protocol:** Monero-style stratum (`login` / `job` / `submit` / `keepalived`), the same one that bytecoin-gui and the [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum) bridge speak. The nonce is at byte 39 of the blob, where both Antminer X3 and XMRig expect it.

The miner built into bytecoin-gui is CPU-only. The pool list with backup pools and "difficulty in the login" come from there; GPU mining comes from XMRig.

**About `cn/0` in XMRig.** XMRig's autoconfig deliberately disables `cn/0` on every backend, so a plain config fails with `incompatible/disabled algorithm "cn/0"`. The alias `"cn/0": "cn"` does not help either, because the `cn` profile does not exist yet when the config is read. So on first start the miner runs the engine once (up to 30 s) with autosave. The engine tunes the `cn` profile itself: it has the same 2 MB scratchpad and kernels as `cn/0` and takes into account the L3 size and the parameters of every GPU. The miner copies this profile under the `cn/0` key and caches it in `probe.json`. Tuning runs again when you press Devices → Check again, or when GPUs, drivers or the engine version change. Disabled GPUs are simply filtered out by index.

## Graphics cards

| Vendor | Backend | Generations | Notes |
| --- | --- | --- | --- |
| NVIDIA | CUDA (`xmrig-cuda.dll` plugin) | Pascal (GTX 10), Turing (GTX 16, RTX 20), Ampere (RTX 30), Ada (RTX 40), Blackwell (RTX 50), professional RTX and data-center GPUs | RTX 50 needs a plugin built with CUDA 12.8+; the miner picks the newest build your driver can run (CUDA 12.9 needs driver 527.41+, older drivers get 11.8 or 10.2). Temperature, power and fan come from NVML. |
| AMD | OpenCL | GCN 4 (RX 400/500), Vega, RDNA 1–4 (RX 5000–9000), Radeon Pro, Instinct | Temperature and power come from ADL. |
| Intel | OpenCL | Arc A (Alchemist), Arc B (Battlemage) | Experimental. Choose Devices → OpenCL platform → Intel. |
| Integrated graphics | — | Intel UHD/Iris, Radeon in APUs | Not used: it shares memory and cooling with the CPU. |

XMRig supports one OpenCL platform at a time. In Auto mode the miner picks AMD when a discrete Radeon is present, otherwise Intel Arc. Individual cards can be switched off on the Devices screen; the switches appear after the first run, once the engine reports device numbers.

When any GPU reaches the limit (80 °C by default), mining pauses. It resumes once the card has cooled down by 8 °C.

![Devices screen](docs/screenshots/devices-light.png)

## Pool or solo

- **Pool:** enter `host:port` of the primary pool and up to two backup pools, plus your public BCN address. The miner never needs keys or a seed. New installs start with the community pool `pool.bytecoin.community:3333` (0% fee, PPLNS, <https://pool.bytecoin.community>); the "Community pool" button on Pool & node puts it back as the primary pool.
- **Solo on your own node:** bytecoind v3.5 has no stratum for miners, so the [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum) bridge has to run next to the node. Rewards go to the address set in the bridge (`BCN_WALLET_ADDRESS`); the worker name only appears in the bridge log. Keep ports 3333 and 8081 inside your LAN.

Pool & node → Test connection sends a single `login` and shows the block height, algorithm and share difficulty.

**Share difficulty.** In Auto mode the miner sends `d=N` to the bridge in the password, aiming at about one share every 30 s. After start it checks the hashrate and, if needed, restarts the engine with a new difficulty. The bridge minimum is 1000.

## Load profiles

| Profile | CPU | NVIDIA |
| --- | --- | --- |
| Eco | lowest priority | bfactor 10, pauses between batches |
| Balanced | below-normal priority | bfactor 6 |
| Maximum | normal priority, no concessions to the system | bfactor 2 |

The CPU threads slider sets an upper limit. CryptoNight needs 2 MB of L3 per thread, and the engine lowers the thread count itself when the cache is too small.

## Languages

Українська, English, Español, Português, Türkçe, Bahasa Indonesia, Tiếng Việt, 中文 — Settings → Appearance → Language. The interface switches at once; mining, the log and the chart keep running. "System" follows the Windows UI language and falls back to English.

Translations live in `src/lang/<code>.txt` as `key = text` lines (`{0}` is a placeholder, `\n` a line break) and are embedded at build time. Missing keys fall back to English. To fix a translation without rebuilding, put `<code>.txt` into a `lang` folder next to `BCNMiner.exe`; its lines override the built-in ones. Only Ukrainian and English were written by hand; the others are machine translations, and reviews by native speakers are welcome.

## What to expect

Bytecoin is mined by Antminer X3 ASICs (~220 kH/s on CryptoNight). A modern GPU gives a few kH/s on `cn/0`, a CPU hundreds of H/s. GPU mining here supports the decentralization of the network rather than making a profit; compare your electricity price with the reward before running several cards.

XMRig gives 1 % of the time to its developers as a fee; this is the minimum in official builds.

## Building

Only Windows 10/11 is needed, no build tools:
```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
`dist\` receives `BCNMiner.exe`, `get-engine.ps1`, README and LICENSE. `.\build.ps1 -Release` also packs `release\BCNMiner-<version>-win-portable.zip`.

The code is C# 5, the language version supported by the compiler that ships with .NET Framework (`csc.exe`): no `$"..."`, `?.` or expression-bodied members.

## Files

- `src/App.cs` — startup, single instance, theme and language.
- `src/MainWindow.cs` — the window and five screens (Mining, Pool & node, Devices, Log, Settings), tray, overheat guard, auto difficulty.
- `src/Engine.cs` — XMRig config, `cn/0` tuning, process, HTTP API, Job Object.
- `src/Gpu.cs` — GPUs from WMI: vendor, architecture, real VRAM size from the registry.
- `src/Net.cs` — `host:port` parsing, stratum test, difficulty from target.
- `src/L.cs`, `src/lang/*.txt` — translations.
- `src/Theme.cs`, `src/Styles.xaml`, `src/Ui.cs`, `src/Chart.cs` — the design system and the chart.
- `get-engine.ps1` — engine download with SHA-256 check.

## License

BCN Miner is licensed under the [GNU LGPL v3.0](LICENSE) (which supplements the [GNU GPL v3.0](COPYING)). XMRig and xmrig-cuda are GPL-3.0; they are not part of this repository and are downloaded from their official releases by `get-engine.ps1`.

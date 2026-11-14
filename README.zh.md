# BCN Miner（Windows 版）

[English](README.md) · [Українська](README.uk.md) · [Español](README.es.md) · [Português](README.pt.md) · [Türkçe](README.tr.md) · [Bahasa Indonesia](README.id.md) · [Tiếng Việt](README.vi.md) · **中文**

Bytecoin（BCN）桌面挖矿程序，可使用 CPU 以及 NVIDIA、AMD 和 Intel Arc 显卡挖矿。提供深色和浅色主题，界面支持八种语言。

这是 Bytecoin 社区的独立项目，与 Bytecoin 创始人或 bcndev 无关。

![挖矿界面](docs/screenshots/mining-dark.png)

## 下载与运行

1. 从 [Releases](../../releases) 下载 `BCNMiner-<版本>-win-portable.zip`，解压到任意位置，U 盘也可以。
2. 安装挖矿引擎。在解压后的文件夹中运行：
   ```
   powershell -ExecutionPolicy Bypass -File .\get-engine.ps1
   ```
   脚本会从 GitHub 下载 [XMRig](https://github.com/xmrig/xmrig) 官方版本并校验 SHA-256。如果电脑装有 NVIDIA 显卡，还会添加 xmrig-cuda 插件。
3. 运行 `BCNMiner.exe`。

无需安装任何组件：Windows 10 和 11 自带 .NET Framework 4.8。

Windows Defender 经常把挖矿程序标记为“可能不需要的软件”。如果 `xmrig.exe` 不见了，请自行把 `engine` 文件夹加入 Defender 的排除项。

### 便携模式

发布的压缩包里有一个 `data` 文件夹。只要它在 `BCNMiner.exe` 旁边，设置、日志和调校缓存都保存在其中，因此整个文件夹可以直接拷到另一台电脑。删除 `data` 后，设置会保存在 `%APPDATA%\BCN Miner`。“设置 → 关于”会显示当前使用的模式。

“随 Windows 启动”开关会写入注册表（`HKCU\...\Run`），且只在您打开它时才写入。

## 工作原理

- **界面：** `BCNMiner.exe`，基于 .NET Framework 4.8 的 WPF。
- **引擎：** 使用 `cn/0` 算法的 XMRig，即 Bytecoin v3.5（区块 v4）仍在使用的原始 CryptoNight。矿工程序以子进程方式启动 XMRig，为它写入配置，并通过其本地 HTTP API 读取统计数据（仅 `127.0.0.1`，随机令牌）。矿工程序关闭或崩溃时，引擎也会随之停止。
- **协议：** Monero 风格的 stratum（`login` / `job` / `submit` / `keepalived`），与 bytecoin-gui 和 [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum) 桥接使用的协议相同。nonce 位于 blob 的第 39 字节，Antminer X3 和 XMRig 都在这个位置读取它。

bytecoin-gui 内置的矿工只支持 CPU。带备用矿池的矿池列表和“在登录中指定难度”的做法来自那里；GPU 挖矿由 XMRig 提供。

**关于 XMRig 中的 `cn/0`。** XMRig 的自动配置会在所有后端故意禁用 `cn/0`，所以普通配置会报错 `incompatible/disabled algorithm "cn/0"`。别名 `"cn/0": "cn"` 也无效，因为读取配置时 `cn` 配置档还不存在。因此首次启动时，矿工程序会以自动保存模式运行一次引擎（最长 30 秒）。引擎会自行调校 `cn` 配置档：它与 `cn/0` 使用相同的 2 MB scratchpad 和内核，并考虑三级缓存大小和每块 GPU 的参数。矿工程序把这个配置档复制到 `cn/0` 键下，并缓存到 `probe.json`。点击“设备 → 重新检测”，或者 GPU、驱动、引擎版本发生变化时，会重新调校。被关闭的 GPU 只是按编号过滤掉。

## 显卡

| 厂商 | 后端 | 代际 | 说明 |
| --- | --- | --- | --- |
| NVIDIA | CUDA（`xmrig-cuda.dll` 插件） | Pascal（GTX 10）、Turing（GTX 16、RTX 20）、Ampere（RTX 30）、Ada（RTX 40）、Blackwell（RTX 50）、专业 RTX 和数据中心 GPU | RTX 50 需要用 CUDA 12.8+ 构建的插件；`get-engine.ps1` 会选择最新的版本（撰写时为 CUDA 12.9）。温度、功耗和风扇数据来自 NVML。 |
| AMD | OpenCL | GCN 4（RX 400/500）、Vega、RDNA 1–4（RX 5000–9000）、Radeon Pro、Instinct | 温度和功耗数据来自 ADL。 |
| Intel | OpenCL | Arc A（Alchemist）、Arc B（Battlemage） | 实验性支持。请选择“设备 → OpenCL 平台 → Intel”。 |
| 集成显卡 | — | Intel UHD/Iris、APU 中的 Radeon | 不使用：它与 CPU 共享内存和散热。 |

XMRig 同一时间只支持一个 OpenCL 平台。在“自动”模式下，有独立 Radeon 显卡时选择 AMD，否则选择 Intel Arc。每块显卡都可以在“设备”页面单独关闭；首次运行后、引擎报告设备编号时，这些开关才会出现。

任一 GPU 达到温度上限（默认 80 °C）时，挖矿会暂停，显卡降温 8 °C 后自动继续。

![设备界面](docs/screenshots/devices-light.png)

## 矿池还是单挖

- **矿池：** 输入主矿池和最多两个备用矿池的 `host:port`，以及您的 BCN 公开地址。矿工程序永远不需要私钥或助记词。新安装默认使用社区矿池 `pool.bytecoin.community:3333`（0% 手续费，PPLNS，<https://pool.bytecoin.community>）；“矿池与节点”页面上的“社区矿池”按钮可将其恢复为主矿池。
- **在自己的节点上单挖：** bytecoind v3.5 不为矿工提供 stratum，因此节点旁需要运行 [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum) 桥接。奖励发送到桥接中设置的地址（`BCN_WALLET_ADDRESS`）；矿工名只出现在桥接日志里。请只在局域网内开放 3333 和 8081 端口。

“矿池与节点 → 测试连接”会发送一次 `login`，并显示区块高度、算法和份额难度。

**份额难度。** 在“自动”模式下，矿工程序通过密码向桥接发送 `d=N`，目标约为每 30 秒一个份额。启动后它会检查算力，必要时以新的难度重启引擎。桥接的最低难度为 1000。

## 负载模式

| 模式 | CPU | NVIDIA |
| --- | --- | --- |
| 节能 | 最低优先级 | bfactor 10，批次之间暂停 |
| 均衡 | 低于正常的优先级 | bfactor 6 |
| 最大 | 普通优先级，不为系统让步 | bfactor 2 |

CPU 线程滑块设置的是上限。CryptoNight 每个线程需要 2 MB 三级缓存，缓存不足时引擎会自行减少线程数。

## 语言

Українська、English、Español、Português、Türkçe、Bahasa Indonesia、Tiếng Việt、中文——“设置 → 外观 → 语言”。界面会立即切换，挖矿、日志和图表都不会中断。“跟随系统”使用 Windows 的界面语言，若不在支持范围内则使用英语。

翻译文件位于 `src/lang/<代码>.txt`，每行格式为 `键 = 文本`（`{0}` 是占位符，`\n` 是换行），构建时会嵌入程序。缺少的键使用英语。若想不重新构建就修正翻译，可把 `<代码>.txt` 放到 `BCNMiner.exe` 旁的 `lang` 文件夹中，其中的行会覆盖内置内容。只有乌克兰语和英语是人工撰写的，其余语言为机器翻译，欢迎母语使用者帮助校对。

## 预期收益

Bytecoin 主要由 Antminer X3 ASIC 挖掘（CryptoNight 约 220 kH/s）。现代 GPU 在 `cn/0` 上约为几 kH/s，CPU 约为几百 H/s。在这里用 GPU 挖矿更多是为了支持网络的去中心化，而不是为了盈利；运行多块显卡之前，请先比较电费与奖励。

XMRig 会把 1 % 的时间作为开发者费用；这是官方版本的最低值。

## 构建

只需要 Windows 10/11，无需任何构建工具：
```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
`dist\` 中会生成 `BCNMiner.exe`、`get-engine.ps1`、README 和许可证。`.\build.ps1 -Release` 还会打包 `release\BCNMiner-<版本>-win-portable.zip`。

代码使用 C# 5，这是 .NET Framework 自带编译器（`csc.exe`）支持的语言版本：不能使用 `$"..."`、`?.` 或表达式主体成员。

## 文件

- `src/App.cs` —— 启动、单实例、主题和语言。
- `src/MainWindow.cs` —— 窗口和五个页面（挖矿、矿池与节点、设备、日志、设置）、托盘、过热保护、自动难度。
- `src/Engine.cs` —— XMRig 配置、`cn/0` 调校、进程、HTTP API、Job Object。
- `src/Gpu.cs` —— 通过 WMI 获取 GPU：厂商、架构、从注册表读取的真实显存大小。
- `src/Net.cs` —— 解析 `host:port`、stratum 测试、根据 target 计算难度。
- `src/L.cs`、`src/lang/*.txt` —— 翻译。
- `src/Theme.cs`、`src/Styles.xaml`、`src/Ui.cs`、`src/Chart.cs` —— 设计系统和图表。
- `get-engine.ps1` —— 带 SHA-256 校验的引擎下载脚本。

## 许可证

BCN Miner 采用 [GNU LGPL v3.0](LICENSE) 许可证（作为 [GNU GPL v3.0](COPYING) 的补充）。XMRig 和 xmrig-cuda 采用 GPL-3.0；它们不属于本仓库，由 `get-engine.ps1` 从官方发布版本下载。

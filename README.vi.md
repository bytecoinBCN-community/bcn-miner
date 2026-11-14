# BCN Miner cho Windows

[English](README.md) · [Українська](README.uk.md) · [Español](README.es.md) · [Português](README.pt.md) · [Türkçe](README.tr.md) · [Bahasa Indonesia](README.id.md) · **Tiếng Việt** · [中文](README.zh.md)

Trình đào Bytecoin (BCN) trên máy tính, đào bằng CPU và card đồ họa NVIDIA, AMD, Intel Arc. Có giao diện sáng và tối, hỗ trợ tám ngôn ngữ.

Đây là dự án độc lập của cộng đồng Bytecoin, không liên quan tới các nhà sáng lập Bytecoin hay bcndev.

![Màn hình đào](docs/screenshots/mining-dark.png)

## Tải về và chạy

1. Tải `BCNMiner-<phiên bản>-win-portable.zip` từ [Releases](../../releases) và giải nén ở bất kỳ đâu, kể cả USB.
2. Cài bộ máy đào. Trong thư mục vừa giải nén, chạy:
   ```
   powershell -ExecutionPolicy Bypass -File .\get-engine.ps1
   ```
   Tập lệnh tải bản phát hành chính thức của [XMRig](https://github.com/xmrig/xmrig) từ GitHub và kiểm tra SHA-256. Nếu máy có card NVIDIA, tập lệnh thêm cả plugin xmrig-cuda.
3. Chạy `BCNMiner.exe`.

Không cần cài gì thêm: .NET Framework 4.8 có sẵn trong Windows 10 và 11.

Windows Defender thường đánh dấu trình đào là "phần mềm có thể không mong muốn". Nếu `xmrig.exe` biến mất, hãy tự thêm thư mục `engine` vào danh sách loại trừ của Defender.

### Chế độ di động

Tệp zip phát hành có thư mục `data`. Khi thư mục này nằm cạnh `BCNMiner.exe`, cài đặt, nhật ký và bộ nhớ đệm tinh chỉnh được lưu trong đó, nên có thể mang cả thư mục sang máy khác. Xóa `data` để lưu cài đặt trong `%APPDATA%\BCN Miner`. Cài đặt → Giới thiệu cho biết chế độ nào đang bật.

Công tắc "Khởi động cùng Windows" ghi vào registry (`HKCU\...\Run`), và chỉ khi bạn bật nó.

## Cách hoạt động

- **Giao diện:** `BCNMiner.exe`, WPF trên .NET Framework 4.8.
- **Bộ máy:** XMRig với thuật toán `cn/0`, tức CryptoNight gốc mà Bytecoin v3.5 (khối v4) vẫn dùng. Trình đào khởi chạy XMRig như tiến trình con, ghi cấu hình cho nó và đọc thống kê qua API HTTP cục bộ (chỉ `127.0.0.1`, token ngẫu nhiên). Nếu trình đào đóng hoặc bị lỗi, bộ máy cũng dừng theo.
- **Giao thức:** stratum kiểu Monero (`login` / `job` / `submit` / `keepalived`), giống giao thức của bytecoin-gui và cầu [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum). Nonce nằm ở byte 39 của blob, đúng vị trí mà cả Antminer X3 lẫn XMRig mong đợi.

Trình đào tích hợp trong bytecoin-gui chỉ dùng CPU. Danh sách pool có pool dự phòng và cách "đặt độ khó trong login" lấy từ đó; khả năng đào bằng GPU đến từ XMRig.

**Về `cn/0` trong XMRig.** Tính năng tự cấu hình của XMRig cố ý tắt `cn/0` trên mọi backend, nên cấu hình thông thường sẽ báo lỗi `incompatible/disabled algorithm "cn/0"`. Bí danh `"cn/0": "cn"` cũng không giúp được, vì khi đọc cấu hình thì cấu hình `cn` chưa tồn tại. Vì vậy, ở lần chạy đầu tiên, trình đào chạy bộ máy một lần (tối đa 30 giây) với chế độ tự lưu. Bộ máy tự tinh chỉnh cấu hình `cn`: cấu hình này dùng cùng scratchpad 2 MB và cùng kernel với `cn/0`, đồng thời tính tới dung lượng L3 và thông số của từng GPU. Trình đào sao chép cấu hình này sang khóa `cn/0` và lưu đệm trong `probe.json`. Việc tinh chỉnh được chạy lại khi bạn nhấn Thiết bị → Kiểm tra lại, hoặc khi GPU, trình điều khiển hay phiên bản bộ máy thay đổi. GPU bị tắt chỉ đơn giản được lọc theo chỉ số.

## Card đồ họa

| Hãng | Backend | Thế hệ | Ghi chú |
| --- | --- | --- | --- |
| NVIDIA | CUDA (plugin `xmrig-cuda.dll`) | Pascal (GTX 10), Turing (GTX 16, RTX 20), Ampere (RTX 30), Ada (RTX 40), Blackwell (RTX 50), RTX chuyên nghiệp và GPU trung tâm dữ liệu | RTX 50 cần plugin được biên dịch với CUDA 12.8+; `get-engine.ps1` lấy bản mới nhất (CUDA 12.9 vào thời điểm viết). Nhiệt độ, công suất và quạt lấy từ NVML. |
| AMD | OpenCL | GCN 4 (RX 400/500), Vega, RDNA 1–4 (RX 5000–9000), Radeon Pro, Instinct | Nhiệt độ và công suất lấy từ ADL. |
| Intel | OpenCL | Arc A (Alchemist), Arc B (Battlemage) | Thử nghiệm. Chọn Thiết bị → Nền tảng OpenCL → Intel. |
| Đồ họa tích hợp | — | Intel UHD/Iris, Radeon trong APU | Không dùng: dùng chung bộ nhớ và tản nhiệt với CPU. |

XMRig chỉ hỗ trợ một nền tảng OpenCL mỗi lần. Ở chế độ Tự động, trình đào chọn AMD nếu có Radeon rời, nếu không thì chọn Intel Arc. Từng card có thể tắt riêng ở màn hình Thiết bị; công tắc xuất hiện sau lần chạy đầu tiên, khi bộ máy báo số thứ tự thiết bị.

Khi bất kỳ GPU nào chạm giới hạn (mặc định 80 °C), việc đào tạm dừng. Việc đào tiếp tục khi card nguội đi 8 °C.

![Màn hình thiết bị](docs/screenshots/devices-light.png)

## Pool hay solo

- **Pool:** nhập `host:port` của pool chính và tối đa hai pool dự phòng, cùng địa chỉ BCN công khai của bạn. Trình đào không bao giờ cần khóa hay seed. Bản cài mới bắt đầu với pool cộng đồng `pool.bytecoin.community:3333` (phí 0%, PPLNS, <https://pool.bytecoin.community>); nút "Pool cộng đồng" ở màn hình Pool và nút đặt lại nó làm pool chính.
- **Solo trên nút riêng:** bytecoind v3.5 không cung cấp stratum cho thợ đào, nên cầu [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum) phải chạy cạnh nút. Phần thưởng gửi tới địa chỉ cấu hình trong cầu (`BCN_WALLET_ADDRESS`); tên worker chỉ hiện trong nhật ký của cầu. Giữ các cổng 3333 và 8081 trong mạng nội bộ.

Pool và nút → Kiểm tra kết nối gửi một yêu cầu `login` và hiện chiều cao khối, thuật toán và độ khó share.

**Độ khó share.** Ở chế độ Tự động, trình đào gửi `d=N` cho cầu qua mật khẩu, nhắm khoảng một share mỗi 30 giây. Sau khi khởi động, nó kiểm tra hashrate và nếu cần sẽ khởi động lại bộ máy với độ khó mới. Mức tối thiểu của cầu là 1000.

## Mức tải

| Mức | CPU | NVIDIA |
| --- | --- | --- |
| Tiết kiệm | ưu tiên thấp nhất | bfactor 10, nghỉ giữa các lô |
| Cân bằng | ưu tiên dưới mức bình thường | bfactor 6 |
| Tối đa | ưu tiên bình thường, không nhường hệ thống | bfactor 2 |

Thanh trượt luồng CPU đặt giới hạn trên. CryptoNight cần 2 MB L3 cho mỗi luồng, và bộ máy tự giảm số luồng khi bộ nhớ đệm quá nhỏ.

## Ngôn ngữ

Українська, English, Español, Português, Türkçe, Bahasa Indonesia, Tiếng Việt, 中文 — Cài đặt → Giao diện → Ngôn ngữ. Giao diện đổi ngay lập tức; việc đào, nhật ký và biểu đồ vẫn tiếp tục chạy. "Theo hệ thống" dùng ngôn ngữ giao diện của Windows và chuyển sang tiếng Anh nếu ngôn ngữ đó không có.

Bản dịch nằm trong `src/lang/<mã>.txt` dưới dạng các dòng `khóa = văn bản` (`{0}` là chỗ giữ chỗ, `\n` là xuống dòng) và được nhúng khi biên dịch. Khóa bị thiếu sẽ lấy từ tiếng Anh. Để sửa bản dịch mà không cần biên dịch lại, đặt `<mã>.txt` vào thư mục `lang` cạnh `BCNMiner.exe`; các dòng của nó sẽ thay thế bản tích hợp. Chỉ tiếng Ukraina và tiếng Anh được viết tay; các ngôn ngữ khác là bản dịch máy, rất mong người bản ngữ góp ý.

## Kỳ vọng thực tế

Bytecoin được đào bằng ASIC Antminer X3 (~220 kH/s trên CryptoNight). Một GPU hiện đại cho vài kH/s trên `cn/0`, còn CPU cho hàng trăm H/s. Ở đây, đào bằng GPU chủ yếu để hỗ trợ phi tập trung hóa mạng hơn là kiếm lời; hãy so sánh giá điện với phần thưởng trước khi chạy nhiều card.

XMRig dành 1 % thời gian làm phí cho nhà phát triển; đây là mức tối thiểu trong các bản chính thức.

## Biên dịch

Chỉ cần Windows 10/11, không cần công cụ biên dịch:
```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
Thư mục `dist\` sẽ có `BCNMiner.exe`, `get-engine.ps1`, README và giấy phép. `.\build.ps1 -Release` còn đóng gói `release\BCNMiner-<phiên bản>-win-portable.zip`.

Mã nguồn là C# 5, phiên bản ngôn ngữ mà trình biên dịch đi kèm .NET Framework (`csc.exe`) hỗ trợ: không có `$"..."`, `?.` hay thành viên thân biểu thức.

## Tệp

- `src/App.cs` — khởi động, một phiên bản duy nhất, chủ đề và ngôn ngữ.
- `src/MainWindow.cs` — cửa sổ và năm màn hình (Đào, Pool và nút, Thiết bị, Nhật ký, Cài đặt), khay hệ thống, bảo vệ quá nhiệt, độ khó tự động.
- `src/Engine.cs` — cấu hình XMRig, tinh chỉnh `cn/0`, tiến trình, API HTTP, Job Object.
- `src/Gpu.cs` — GPU từ WMI: hãng, kiến trúc, dung lượng VRAM thực từ registry.
- `src/Net.cs` — phân tích `host:port`, kiểm tra stratum, độ khó từ target.
- `src/L.cs`, `src/lang/*.txt` — bản dịch.
- `src/Theme.cs`, `src/Styles.xaml`, `src/Ui.cs`, `src/Chart.cs` — hệ thống thiết kế và biểu đồ.
- `get-engine.ps1` — tải bộ máy có kiểm tra SHA-256.

## Giấy phép

BCN Miner được phát hành theo giấy phép [GNU LGPL v3.0](LICENSE) (bổ sung cho [GNU GPL v3.0](COPYING)). XMRig và xmrig-cuda dùng giấy phép GPL-3.0; chúng không nằm trong kho mã này và được `get-engine.ps1` tải từ bản phát hành chính thức.

# Windows için BCN Miner

[English](README.md) · [Українська](README.uk.md) · [Español](README.es.md) · [Português](README.pt.md) · **Türkçe** · [Bahasa Indonesia](README.id.md) · [Tiếng Việt](README.vi.md) · [中文](README.zh.md)

Bytecoin (BCN) için CPU'da ve NVIDIA, AMD ve Intel Arc ekran kartlarında kazı yapan bir masaüstü madencisi. Koyu ve açık teması, sekiz dilde arayüzü vardır.

Bytecoin topluluğunun bağımsız bir projesidir; Bytecoin'in kurucularıyla veya bcndev ile bağlantısı yoktur.

![Madencilik ekranı](docs/screenshots/mining-dark.png)

## İndirme ve çalıştırma

1. [bytecoin.community](https://bytecoin.community/#downloads) sayfasından `BCNMiner-<sürüm>-win-portable.zip` dosyasını indirin ve istediğiniz yere, bir USB belleğe bile açın.
2. Madencilik motorunu kurun. Açtığınız klasörde şunu çalıştırın:
   ```
   powershell -ExecutionPolicy Bypass -File .\get-engine.ps1
   ```
   Betik, resmi [XMRig](https://github.com/xmrig/xmrig) sürümünü GitHub'dan indirir ve SHA-256 değerini doğrular. Bilgisayarda NVIDIA kart varsa xmrig-cuda eklentisini de ekler.
3. `BCNMiner.exe` dosyasını çalıştırın.

Hiçbir şey kurmanız gerekmez: .NET Framework 4.8, Windows 10 ve 11 ile birlikte gelir.

Windows Defender madencileri sık sık “istenmeyebilecek yazılım” olarak işaretler. `xmrig.exe` kaybolursa `engine` klasörünü Defender dışlamalarına kendiniz ekleyin.

### Taşınabilir mod

Sürüm arşivinde bir `data` klasörü vardır. Bu klasör `BCNMiner.exe` yanında durduğu sürece ayarlar, günlükler ve ayar önbelleği orada tutulur; böylece tüm klasör başka bir bilgisayara taşınabilir. Ayarları `%APPDATA%\BCN Miner` içinde tutmak için `data` klasörünü silin. Ayarlar → Hakkında, hangi modun açık olduğunu gösterir.

“Windows ile başlat” anahtarı kayıt defterine (`HKCU\...\Run`) yazar, o da yalnızca siz açtığınızda.

## Nasıl çalışır

- **Arayüz:** `BCNMiner.exe`, .NET Framework 4.8 üzerinde WPF.
- **Motor:** `cn/0` algoritmasıyla XMRig; Bytecoin v3.5'in (blok v4) hâlâ kullandığı orijinal CryptoNight. Madenci XMRig'i alt süreç olarak başlatır, yapılandırmasını yazar ve istatistikleri yerel HTTP API'sinden okur (yalnızca `127.0.0.1`, rastgele belirteç). Madenci kapanır veya çökerse motor da onunla birlikte durur.
- **Protokol:** Monero tarzı stratum (`login` / `job` / `submit` / `keepalived`); bytecoin-gui ve [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum) köprüsünün kullandığı protokolün aynısı. Nonce, blob'un 39. baytındadır; hem Antminer X3 hem XMRig onu orada bekler.

bytecoin-gui'deki yerleşik madenci yalnızca CPU kullanır. Yedek havuzlu havuz listesi ve “login'de zorluk” fikri oradan gelir; GPU madenciliğini XMRig sağlar.

**XMRig'de `cn/0` hakkında.** XMRig'in otomatik yapılandırması `cn/0`'ı tüm arka uçlarda bilerek kapatır, bu yüzden düz bir yapılandırma `incompatible/disabled algorithm "cn/0"` hatası verir. `"cn/0": "cn"` takma adı da işe yaramaz, çünkü yapılandırma okunurken `cn` profili henüz yoktur. Bu nedenle ilk açılışta madenci motoru bir kez (en fazla 30 sn) otomatik kayıtla çalıştırır. Motor `cn` profilini kendisi ayarlar: profil `cn/0` ile aynı 2 MB scratchpad'i ve çekirdekleri kullanır, L3 boyutunu ve her GPU'nun parametrelerini hesaba katar. Madenci bu profili `cn/0` anahtarının altına kopyalar ve `probe.json` dosyasında önbelleğe alır. Cihazlar → Yeniden denetle'ye basıldığında ya da GPU'lar, sürücüler veya motor sürümü değiştiğinde ayar yeniden yapılır. Kapatılan GPU'lar dizin numarasına göre ayıklanır.

## Ekran kartları

| Üretici | Arka uç | Nesiller | Notlar |
| --- | --- | --- | --- |
| NVIDIA | CUDA (`xmrig-cuda.dll` eklentisi) | Pascal (GTX 10), Turing (GTX 16, RTX 20), Ampere (RTX 30), Ada (RTX 40), Blackwell (RTX 50), profesyonel RTX ve veri merkezi GPU'ları | RTX 50, CUDA 12.8+ ile derlenmiş eklenti ister; `get-engine.ps1` en yeni sürümü alır (yazıldığı sırada CUDA 12.9). Sıcaklık, güç ve fan NVML'den gelir. |
| AMD | OpenCL | GCN 4 (RX 400/500), Vega, RDNA 1–4 (RX 5000–9000), Radeon Pro, Instinct | Sıcaklık ve güç ADL'den gelir. |
| Intel | OpenCL | Arc A (Alchemist), Arc B (Battlemage) | Deneysel. Cihazlar → OpenCL platformu → Intel'i seçin. |
| Tümleşik grafik | — | Intel UHD/Iris, APU'lardaki Radeon | Kullanılmaz: belleği ve soğutmayı CPU ile paylaşır. |

XMRig aynı anda tek bir OpenCL platformunu destekler. Otomatik modda madenci, harici bir Radeon varsa AMD'yi, yoksa Intel Arc'ı seçer. Her kart Cihazlar ekranında ayrı ayrı kapatılabilir; anahtarlar ilk çalıştırmadan sonra, motor cihaz numaralarını bildirdiğinde görünür.

Herhangi bir GPU sınıra ulaştığında (varsayılan 80 °C) madencilik duraklar. Kart 8 °C soğuyunca devam eder.

![Cihazlar ekranı](docs/screenshots/devices-light.png)

## Havuz mu, solo mu

- **Havuz:** ana havuzun ve en fazla iki yedek havuzun `host:port` adresini, ayrıca herkese açık BCN adresinizi girin. Madenci hiçbir zaman anahtar veya seed istemez. Yeni kurulumlar topluluk havuzu `pool.bytecoin.community:3333` ile başlar (%0 ücret, PPLNS, <https://pool.bytecoin.community>); Havuz ve düğüm ekranındaki “Topluluk havuzu” düğmesi onu yeniden ana havuz yapar.
- **Kendi düğümünüzde solo:** bytecoind v3.5 madencilere stratum sunmaz, bu yüzden düğümün yanında [bcn-stratum](https://github.com/bytecoinBCN-community/bcn-stratum) köprüsü çalışmalıdır. Ödül, köprüde ayarlanan adrese (`BCN_WALLET_ADDRESS`) gider; worker adı yalnızca köprü günlüğünde görünür. 3333 ve 8081 portlarını yerel ağınızda tutun.

Havuz ve düğüm → Bağlantıyı test et, tek bir `login` gönderir ve blok yüksekliğini, algoritmayı ve share zorluğunu gösterir.

**Share zorluğu.** Otomatik modda madenci, yaklaşık 30 sn'de bir share hedefleyerek köprüye parolada `d=N` gönderir. Başladıktan sonra hashrate'i kontrol eder ve gerekirse motoru yeni zorlukla yeniden başlatır. Köprüdeki en düşük değer 1000'dir.

## Yük profilleri

| Profil | CPU | NVIDIA |
| --- | --- | --- |
| Eko | en düşük öncelik | bfactor 10, paketler arasında duraklama |
| Dengeli | normalin altında öncelik | bfactor 6 |
| En yüksek | normal öncelik, sisteme taviz yok | bfactor 2 |

CPU iş parçacığı kaydırıcısı üst sınırı belirler. CryptoNight iş parçacığı başına 2 MB L3 ister; önbellek küçükse motor iş parçacığı sayısını kendisi düşürür.

## Diller

Українська, English, Español, Português, Türkçe, Bahasa Indonesia, Tiếng Việt, 中文 — Ayarlar → Görünüm → Dil. Arayüz anında değişir; madencilik, günlük ve grafik çalışmaya devam eder. “Sistem”, Windows arayüz dilini izler; o dil yoksa İngilizce kullanılır.

Çeviriler `src/lang/<kod>.txt` dosyalarında `anahtar = metin` satırları olarak durur (`{0}` bir yer tutucudur, `\n` satır sonudur) ve derleme sırasında programa gömülür. Eksik anahtarlar İngilizceden alınır. Bir çeviriyi yeniden derlemeden düzeltmek için `<kod>.txt` dosyasını `BCNMiner.exe` yanındaki `lang` klasörüne koyun; satırları yerleşik olanların yerine geçer. Yalnızca Ukraynaca ve İngilizce elle yazıldı; diğer diller makine çevirisidir, ana dili konuşanların düzeltmeleri memnuniyetle karşılanır.

## Ne beklemeli

Bytecoin, Antminer X3 ASIC'leri ile kazılır (CryptoNight'ta ~220 kH/s). Modern bir GPU `cn/0`'da birkaç kH/s, bir CPU ise yüzlerce H/s verir. Burada GPU madenciliği kâr etmekten çok ağın merkeziyetsizliğini destekler; birkaç kart çalıştırmadan önce elektrik fiyatını ödülle karşılaştırın.

XMRig, sürenin %1'ini geliştiricilerine ücret olarak ayırır; resmi sürümlerde bu en düşük değerdir.

## Derleme

Yalnızca Windows 10/11 gerekir, derleme araçları gerekmez:
```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
`dist\` klasörüne `BCNMiner.exe`, `get-engine.ps1`, README ve lisans gelir. `.\build.ps1 -Release` ayrıca `release\BCNMiner-<sürüm>-win-portable.zip` paketini oluşturur.

Kod C# 5'tir; .NET Framework ile gelen derleyicinin (`csc.exe`) desteklediği dil sürümü budur: `$"..."`, `?.` ve ifade gövdeli üyeler yoktur.

## Dosyalar

- `src/App.cs` — başlangıç, tek örnek, tema ve dil.
- `src/MainWindow.cs` — pencere ve beş ekran (Madencilik, Havuz ve düğüm, Cihazlar, Günlük, Ayarlar), bildirim alanı, aşırı ısınma koruması, otomatik zorluk.
- `src/Engine.cs` — XMRig yapılandırması, `cn/0` ayarı, süreç, HTTP API, Job Object.
- `src/Gpu.cs` — WMI'dan GPU'lar: üretici, mimari, kayıt defterinden gerçek VRAM boyutu.
- `src/Net.cs` — `host:port` ayrıştırma, stratum testi, target'tan zorluk.
- `src/L.cs`, `src/lang/*.txt` — çeviriler.
- `src/Theme.cs`, `src/Styles.xaml`, `src/Ui.cs`, `src/Chart.cs` — tasarım sistemi ve grafik.
- `get-engine.ps1` — SHA-256 doğrulamalı motor indirme.

## Lisans

BCN Miner, [GNU LGPL v3.0](LICENSE) lisansıyla dağıtılır ([GNU GPL v3.0](COPYING) lisansını tamamlar). XMRig ve xmrig-cuda GPL-3.0'dır; bu deponun parçası değildir ve `get-engine.ps1` onları resmi sürümlerinden indirir.

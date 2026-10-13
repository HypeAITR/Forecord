# Forecord — Hafif, Yüksek Kaliteli Ekran Kaydedici

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11%20(x64)-0078D6)](#gereksinimler)

**Forecord**, karmaşık yayıncı ayarlarıyla uğraşmadan **tek tıkla, yüksek kalitede ve sistem performansını düşürmeden** ekran kaydı almak isteyen geliştiriciler, eğitmenler, öğrenciler ve içerik üreticileri için geliştirilmiş açık kaynaklı bir Windows ekran kaydedicisidir.

OBS Studio gibi ağır yayın araçlarına alternatif olarak; doğrudan masaüstü yakalama, canlı ekran üstü çizim (annotation), kayıtta görünmeyen kontrol paneli ve donanım hızlandırmalı kodlama özelliklerini sade bir arayüzde birleştirir.

---

## ✨ Öne Çıkan Özellikler

- **🚀 Düşük Ek Yük (Low-Overhead) Yakalama:**
  - **DXGI Desktop Duplication API** (Direct3D 11) ile doğrudan GPU üzerinden düşük gecikmeli ekran yakalama.
  - Değişmeyen (idle) kareleri atlayan **gerçek zamanlı VFR (Variable Frame Rate)** ve **Dirty-Rect / Move-Rect** kısmi okuma optimizasyonu.
  - Pil durumu ve sistem yüküne göre dinamik kare hızı/preset uyarlaması (**Adaptive Performance Scaling**).
- **🎬 Donanım Hızlandırmalı Kodlama:**
  - Otomatik donanım kodlayıcı tespiti: **NVIDIA NVENC (`h264_nvenc`)** → **AMD AMF (`h264_amf`)** → **Intel QSV (`h264_qsv`)** → **CPU (`libx264`)**.
  - Doğrudan D3D11 GPU dokusu üzerinden kodlama (`NvencGpuEncoder` / `AmfGpuEncoder`) ve yerleşik MP4 paketleme (`H264Mp4Muxer`) altyapısı.
  - **30 / 60 / 120 FPS** ve **Native / 1080p / 1440p / 4K** çözünürlük seçenekleri.
- **🔊 Canlı Ses Kaydı ve Senkronizasyon (Live Audio Muxing):**
  - **WASAPI** üzerinden mikrofon ve sistem sesini (loopback) eşzamanlı kaydetme.
  - Adlandırılmış borular (Named Pipes) ile kayıt esnasında gerçek zamanlı ses-video birleştirme (kayıt bittiğinde uzun bekleme sürelerini ortadan kaldırır).
  - Kayıt sırasında yüzen panelden mikrofonu anlık açıp kapatabilme.
- **👻 Kayıtta Görünmeyen Yüzen Kontrol Paneli:**
  - Windows `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` API'si sayesinde kontrol paneli ekranın üst kısmında durur ancak **kaydedilen videoda görünmez**.
- **✏️ Canlı Ekran Üstü Çizim (Annotation Overlay):**
  - Kayıt sırasında ekranın herhangi bir yerine **Kalem**, **Ok** ve **Silgi** araçlarıyla çizim yapabilme.
  - Çizim aracı kapatıldığında (veya `Esc` tuşuna basıldığında) çizimler ekranda kalmaya devam ederken arka plandaki uygulamalarla normal şekilde çalışmaya devam edebilme (click-through).
- **📸 Sıfır Maliyetli Ekran Görüntüsü (Lazy Screenshot):**
  - Kayıt devam ederken tek tuşla (`Ctrl+Alt+S`) imleçsiz, ham masaüstü karesinden anında PNG ekran görüntüsü alma.
- **⏸️ Kesintisiz Duraklat / Devam Et:**
  - Duraklatılan süreler videoya siyah kare veya boşluk olarak yansımaz; segmentler otomatik ve akıcı biçimde birleştirilir.

---

## 🖥️ Gereksinimler

| Bileşen | Gereksinim |
|---|---|
| **İşletim Sistemi** | Windows 10 (2004 / Build 19041+) veya Windows 11 (x64) |
| **.NET SDK / Runtime** | [.NET 8.0 SDK (Windows x64)](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **FFmpeg** | `ffmpeg.exe` (uygulama dizininde veya sistem `PATH` ortam değişkeninde) |

---

## 🛠️ Kurulum ve Derleme

### 1. Depoyu Klonlayın
```powershell
git clone https://github.com/HypeAITR/Forecord.git
cd Forecord
```

### 2. FFmpeg'i Ekleyin
[Gyan.dev FFmpeg Builds](https://www.gyan.dev/ffmpeg/builds/) veya resmi FFmpeg dağıtımlarından indirdiğiniz `ffmpeg.exe` dosyasını:
- Doğrudan `ScreenRecorder/` proje klasörüne (veya derleme çıktı klasörüne) koyabilir, **veya**
- Sistem `PATH` ortam değişkeninize ekleyebilirsiniz.

### 3. Derleyin ve Çalıştırın
```powershell
cd ScreenRecorder

# Bağımlılıkları yükle ve Release modunda derle
dotnet restore
dotnet build -c Release

# Uygulamayı çalıştır
dotnet run -c Release
```

### Tek Dosyalı Çalıştırılabilir (`.exe`) Üretme
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

---

## ⌨️ Varsayılan Kısayollar

 Tüm kısayollar uygulama içindeki **⚙ Ayarlar** menüsünden özelleştirilebilir:

| İşlev | Varsayılan Kısayol |
|---|---|
| Kaydı Başlat / Durdur | `Ctrl + Alt + R` |
| Duraklat / Devam Et | `Ctrl + Alt + P` |
| Mikrofon Aç / Kapat | `Ctrl + Alt + M` |
| Ekran Görüntüsü Al (Kayıt Sırasında) | `Ctrl + Alt + S` |

### Çizim Modu Kısayolları (Çizim Araç Çubuğu Açıkken)
| İşlev | Kısayol |
|---|---|
| Kalem Aracı | `D` |
| Silgi Aracı | `E` |
| Son Çizimi Geri Al | `Ctrl + Z` |
| Tüm Çizimleri Temizle | `C` |
| Çizim Aracını Bırak (Masaüstü Tıklamasına Dön) | `Esc` |

---

## 📂 Proje Yapısı

```text
Forecord/
└── ScreenRecorder/
    ├── ScreenRecorder.csproj        # .NET 8 WPF proje tanımı (SharpDX & NAudio)
    ├── App.xaml / App.xaml.cs       # Uygulama başlangıcı ve global koyu tema
    ├── MainWindow.xaml(.cs)         # Ana kontrol penceresi
    ├── Helpers/
    │   └── DesktopCapture.cs        # DXGI Desktop Duplication, dirty-rect ve imleç birleştirme
    ├── Models/                      # Ayar, ses cihazı, yakalama modu ve durum modelleri
    ├── Services/
    │   ├── RecordingService.cs      # Kayıt döngüsü, VFR zamanlama, adaptif ölçekleme, segment yönetimi
    │   ├── EncoderService.cs        # FFmpeg süreç yönetimi, donanım kodlayıcı tespiti, canlı muxing
    │   ├── AudioService.cs          # WASAPI mikrofon & sistem sesi yakalama ve Named Pipe akışı
    │   ├── SothelCaptureEngine.cs   # DWM/DXGI yakalama oturumu ve telemetri yönetimi
    │   ├── HotkeyService.cs         # Global Win32 klavye kısayolları
    │   ├── SettingsService.cs       # JSON tabanlı kullanıcı ayarları (%AppData%\Forecord)
    │   ├── FileService.cs           # Çıktı ve geçici segment dosyası yönetimi
    │   ├── GpuEncoding/             # Doğrudan NVIDIA NVENC ve AMD AMF D3D11 kodlayıcı katmanı
    │   └── Muxing/                  # H.264 Annex-B -> MP4 paketleyici (H264Mp4Muxer)
    ├── ViewModels/                  # MVVM görünüm modelleri (Main, Settings, DrawingTool)
    ├── Views/                       # Yüzen panel, çizim katmanı, ayarlar ve bildirim pencereleri
    └── Effects/ & Shaders/          # WPF HLSL piksel shader arka plan efektleri
```

---

## 📄 Lisans

Bu proje **MIT Lisansı** altında lisanslanmıştır. Ayrıntılar için [`LICENSE`](LICENSE) dosyasına göz atabilirsiniz.

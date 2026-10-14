# Forecord

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11%20(x64)-0078D6)](#sistem-gereksinimleri)

**[Türkçe](#türkçe)** | **[English](#english)**

---

## Türkçe

### Genel Bakış

**Forecord**, Windows 10 ve Windows 11 sistemler için `.NET 8` ve `WPF` ile yazılmış açık kaynaklı bir masaüstü ekran ve ses kayıt uygulamasıdır. Harici `ffmpeg.exe` sürecine ihtiyaç duymadan, doğrudan GPU üzerinde H.264 kodlama ve yerleşik MP4 paketleme yapar.

### Teknik Özellikler

- **Masaüstü Yakalama**
  - Direct3D 11 ve **DXGI Desktop Duplication API** ile ekran yakalama.
  - Değişmeyen kareleri atlayan **Değişken Kare Hızı (VFR)** zamanlaması.
  - D3D11 dokusu üzerinde yalnızca imleç bölgesini güncelleyen donanım imleç birleştirme.
  - Sistem yükü ve pil durumuna göre kare hızı sınırını otomatik uyarlayan güç/performans kontrolü.
- **Doğrudan GPU Video Kodlama ve Yerleşik MP4 Paketleme**
  - **NVIDIA NVENC (`nvEncodeAPI64.dll`)** ve **AMD AMF (`amfrt64.dll`)** üzerinden CPU geri okuması olmadan doğrudan D3D11 dokusu kodlama.
  - D3D11 shader tabanlı GPU ölçekleme (`Native`, `720p`, `1080p`, `1440p`, `4K`) ve `30 / 60 / 120 FPS` desteği.
  - Yerleşik **`H264Mp4Muxer`** ile geçici dosya kopyalaması olmadan doğrudan `.mp4` yazımı ve çoklu segment (Duraklat/Devam Et) birleştirme.
- **Canlı Ses Yakalama ve AAC Kodlama**
  - **WASAPI** üzerinden mikrofon ve sistem sesinin (loopback) eşzamanlı kaydı.
  - Video saatiyle (`Stopwatch`) kilitli canlı ses karıştırma ve **Windows Media Foundation AAC-LC MFT** ile kayıt esnasında anlık MP4 ses paketleme.
- **Kayıt Kontrolü ve Ekran Üstü İşaretleme (Annotation)**
  - `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` ile kayıt çıktısında görünmeyen yüzen kontrol paneli.
  - Kayıt sırasında kalem, ok ve silgi araçları sunan tıklama-geçirgen (`WS_EX_TRANSPARENT`) çizim katmanı.
  - Kayıt esnasında talep üzerine (`Ctrl + Alt + S`) imleçsiz PNG ekran görüntüsü alma.

### Sistem Gereksinimleri

| Bileşen | Gereksinim |
|---|---|
| **İşletim Sistemi** | Windows 10 (Sürüm 2004 / Yapı 19041+) veya Windows 11 (x64) |
| **Ekran Kartı** | NVIDIA (NVENC destekli) veya AMD (AMF destekli) GPU |
| **.NET SDK (Yalnızca Kaynaktan Derleme İçin)** | [.NET 8.0 SDK (Windows x64)](https://dotnet.microsoft.com/download/dotnet/8.0) |

### Kaynak Koddan Derleme

```powershell
git clone https://github.com/HypeAITR/Forecord.git
cd Forecord/ScreenRecorder

dotnet restore
dotnet build -c Release
dotnet run -c Release
```

#### Tek Dosyalı Çalıştırılabilir Sürüm Üretimi
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

### Varsayılan Klavye Kısayolları

| İşlev | Varsayılan Kısayol |
|---|---|
| Kaydı Başlat / Durdur | `Ctrl + Alt + R` |
| Duraklat / Devam Et | `Ctrl + Alt + P` |
| Mikrofon Aç / Kapat | `Ctrl + Alt + M` |
| Ekran Görüntüsü Al (Kayıt Sırasında) | `Ctrl + Alt + S` |

**Çizim Katmanı Kısayolları:**

| İşlev | Kısayol |
|---|---|
| Kalem Aracı | `D` |
| Silgi Aracı | `E` |
| Son İşlemi Geri Al | `Ctrl + Z` |
| Tüm Çizimleri Temizle | `C` |
| Çizim Aracını Bırak | `Esc` |

### Dizin Yapısı

```text
Forecord/
└── ScreenRecorder/
    ├── ScreenRecorder.csproj        # Proje yapılandırması ve paket bağımlılıkları
    ├── App.xaml / App.xaml.cs       # Uygulama yaşam döngüsü ve stil tanımları
    ├── MainWindow.xaml(.cs)         # Ana pencere
    ├── Helpers/
    │   └── DesktopCapture.cs        # DXGI Desktop Duplication ve GPU imleç işleme
    ├── Models/                      # Veri ve ayar modelleri
    ├── Services/
    │   ├── RecordingService.cs      # Kayıt durum makinesi ve segment yönetimi
    │   ├── Recording/               # GPU-Direct kayıt döngüsü, adaptif FPS, güç profili, ekran görüntüsü
    │   ├── GpuEncoding/             # Doğrudan NVIDIA NVENC, AMD AMF ve D3D11 GPU ölçekleyici
    │   ├── Muxing/                  # Yerleşik H.264 + AAC MP4 paketleyici (H264Mp4Muxer)
    │   ├── Audio/                   # Saat-kilitli WASAPI ses karıştırıcı ve Media Foundation AAC kodlayıcı
    │   ├── Diagnostics/             # Hata günlükleme ve bileşen öz-testi (--self-test)
    │   ├── HotkeyService.cs         # Win32 global klavye kısayolları
    │   ├── SettingsService.cs       # Kullanıcı yapılandırma yönetimi (%AppData%\Forecord)
    │   └── FileService.cs           # Çıktı ve geçici dosya yönetimi
    ├── ViewModels/                  # MVVM görünüm modelleri
    ├── Views/                       # Arayüz pencereleri ve çizim katmanı
    └── Effects/ & Shaders/          # WPF HLSL piksel shader bileşenleri
```

### Lisans

Bu proje **MIT Lisansı** kapsamında sunulmaktadır. Ayrıntılar için [`LICENSE`](LICENSE) dosyasına bakınız.

---

## English

### Overview

**Forecord** is an open-source desktop screen and audio recorder for Windows 10 and Windows 11 built with `.NET 8` and `WPF`. It encodes video directly on the GPU and writes MP4 files natively in-process without requiring an external `ffmpeg.exe` binary.

### Technical Features

- **Desktop Capture**
  - Direct3D 11 and **DXGI Desktop Duplication API** frame acquisition.
  - **Variable Frame Rate (VFR)** pacing that skips duplicate idle frames.
  - Sub-region GPU hardware cursor compositing directly on D3D11 textures.
  - Automatic frame rate and power profile scaling based on system load and battery status.
- **Direct GPU Video Encoding and Native MP4 Muxing**
  - Direct D3D11 texture encoding via **NVIDIA NVENC (`nvEncodeAPI64.dll`)** and **AMD AMF (`amfrt64.dll`)** with zero CPU frame readback.
  - D3D11 shader-based GPU resolution scaling (`Native`, `720p`, `1080p`, `1440p`, `4K`) and `30 / 60 / 120 FPS` targets.
  - Built-in **`H264Mp4Muxer`** with in-place `.mp4` writing and native multi-segment concatenation (Pause/Resume).
- **Live Audio Capture and AAC Encoding**
  - Simultaneous microphone and system audio (loopback) capture via **WASAPI**.
  - Wallclock-synchronized audio mixing locked to the video `Stopwatch` and real-time AAC-LC encoding via **Windows Media Foundation MFT**.
- **Recording Controls and On-Screen Annotation**
  - Floating control bar excluded from capture via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
  - Full-screen transparent annotation overlay supporting pen, arrow, and eraser tools with automatic click-through (`WS_EX_TRANSPARENT`).
  - On-demand (`Ctrl + Alt + S`) cursor-free PNG screenshot capture during recording.

### System Requirements

| Component | Requirement |
|---|---|
| **Operating System** | Windows 10 (Version 2004 / Build 19041+) or Windows 11 (x64) |
| **Graphics Card** | NVIDIA (NVENC-capable) or AMD (AMF-capable) GPU |
| **.NET SDK (Source Build Only)** | [.NET 8.0 SDK (Windows x64)](https://dotnet.microsoft.com/download/dotnet/8.0) |

### Building from Source

```powershell
git clone https://github.com/HypeAITR/Forecord.git
cd Forecord/ScreenRecorder

dotnet restore
dotnet build -c Release
dotnet run -c Release
```

#### Publish as a Single-File Executable
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

### Default Keyboard Shortcuts

| Action | Default Shortcut |
|---|---|
| Start / Stop Recording | `Ctrl + Alt + R` |
| Pause / Resume Recording | `Ctrl + Alt + P` |
| Toggle Microphone | `Ctrl + Alt + M` |
| Capture Screenshot (During Recording) | `Ctrl + Alt + S` |

**Annotation Overlay Shortcuts:**

| Action | Shortcut |
|---|---|
| Pen Tool | `D` |
| Eraser Tool | `E` |
| Undo Last Action | `Ctrl + Z` |
| Clear All Annotations | `C` |
| Release Active Tool | `Esc` |

### Project Structure

```text
Forecord/
└── ScreenRecorder/
    ├── ScreenRecorder.csproj        # Project configuration and NuGet dependencies
    ├── App.xaml / App.xaml.cs       # Application lifecycle and resource dictionaries
    ├── MainWindow.xaml(.cs)         # Main window
    ├── Helpers/
    │   └── DesktopCapture.cs        # DXGI Desktop Duplication and GPU cursor compositing
    ├── Models/                      # Data and settings models
    ├── Services/
    │   ├── RecordingService.cs      # Recording state machine and segment orchestration
    │   ├── Recording/               # GPU-Direct capture loop, adaptive FPS, power profile, screenshots
    │   ├── GpuEncoding/             # Direct NVIDIA NVENC, AMD AMF, and D3D11 texture scaler
    │   ├── Muxing/                  # Native H.264 + AAC MP4 muxer (H264Mp4Muxer)
    │   ├── Audio/                   # Wallclock-synced WASAPI mixer and Media Foundation AAC encoder
    │   ├── Diagnostics/             # Error logging and pipeline self-test (--self-test)
    │   ├── HotkeyService.cs         # Win32 global hotkey registration
    │   ├── SettingsService.cs       # JSON configuration persistence (%AppData%\Forecord)
    │   └── FileService.cs           # Output and temporary file management
    ├── ViewModels/                  # MVVM view models
    ├── Views/                       # UI windows, floating panel, and annotation overlay
    └── Effects/ & Shaders/          # WPF HLSL pixel shader components
```

### License

This project is licensed under the **MIT License**. See [`LICENSE`](LICENSE) for details.

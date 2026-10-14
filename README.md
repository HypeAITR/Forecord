# Forecord

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11%20(x64)-0078D6)](#sistem-gereksinimleri)

**[Türkçe](#türkçe)** | **[English](#english)**

---

## Türkçe

### Genel Bakış

**Forecord**, Windows 10 ve Windows 11 sistemler için `.NET 8` ve `WPF` tabanlı olarak geliştirilmiş açık kaynaklı bir masaüstü ekran kayıt uygulamasıdır. Karmaşık yayın yazılımlarına ihtiyaç duymadan, düşük sistem kaynağı kullanımıyla yüksek kaliteli ekran ve ses kaydı alınmasını sağlamak amacıyla tasarlanmıştır.

Uygulama; doğrudan grafik birimi üzerinden ekran yakalama (DXGI Desktop Duplication), donanım hızlandırmalı video kodlama, gerçek zamanlı ses-görüntü birleştirme (muxing) ve kayıt esnasında ekran üstü işaretleme (annotation) işlevlerini tek bir arayüzde sunar.

### Teknik Özellikler

- **Masaüstü Yakalama Mimarisi**
  - Direct3D 11 ve **DXGI Desktop Duplication API** üzerinden düşük gecikmeli ekran yakalama.
  - Değişmeyen karelerin yeniden işlenmesini önleyen **Değişken Kare Hızı (VFR)** zamanlaması ve **Dirty-Rect / Move-Rect** kısmi bellek okuma mekanizması.
  - Sistem yükü ve güç durumuna (pil/adaptör) göre kare hızı ve kodlayıcı profilini dinamik olarak uyarlayan performans yönetimi.
- **Video Kodlama ve Paketleme**
  - Donanım kodlayıcı desteği ve otomatik önceliklendirme: **NVIDIA NVENC (`h264_nvenc`)**, **AMD AMF (`h264_amf`)**, **Intel QSV (`h264_qsv`)** ve yazılım tabanlı **CPU (`libx264`)** geri dönüşü (fallback).
  - Doğrudan D3D11 GPU dokusu üzerinden kodlama (`NvencGpuEncoder`, `AmfGpuEncoder`) ve yerleşik MP4 konteyner paketleme (`H264Mp4Muxer`) katmanı.
  - 30, 60 ve 120 FPS kare hızı ile Native, 1080p, 1440p ve 4K çıkış çözünürlüğü desteği.
- **Ses Yakalama ve Senkronizasyon**
  - **WASAPI** aracılığıyla mikrofon ve sistem sesinin (loopback) eşzamanlı kaydı.
  - Adlandırılmış borular (Named Pipes) üzerinden kayıt sırasında gerçek zamanlı ses ve video birleştirme.
  - Kayıt devam ederken ses kaynaklarını açıp kapatabilme ve segment bazlı kesintisiz geçiş.
- **Kayıt Kontrolü ve Ekran Üstü İşaretleme (Annotation)**
  - `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` API desteği ile masaüstünde görünür olduğu halde kayıt çıktısında yer almayan yüzen kontrol paneli.
  - Kayıt esnasında ekran üzerinde kalem, ok ve silgi araçlarıyla çizim yapılmasını sağlayan şeffaf katman; araç devre dışı bırakıldığında arka plan uygulamalarıyla etkileşimi engellemeyen tıklama-geçirgen (click-through) yapı.
  - Kayıt sırasında yalnızca talep edildiğinde bellekte örneklenen (lazy snapshot) imleçsiz PNG ekran görüntüsü alma işlevi.
  - Duraklatma ve devam ettirme işlemlerinde segmentlerin otomatik olarak tek bir MP4 dosyasında birleştirilmesi.

### Sistem Gereksinimleri

| Bileşen | Gereksinim |
|---|---|
| **İşletim Sistemi** | Windows 10 (Sürüm 2004 / Yapı 19041 veya üzeri) ya da Windows 11 (x64) |
| **.NET SDK (Yalnızca Kaynaktan Derleme İçin)** | [.NET 8.0 SDK (Windows x64)](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **FFmpeg** | `ffmpeg.exe` (tam sürüm paketinde dahildir; tekil `.exe` kullanımında uygulama dizininde veya `PATH` içinde bulunmalıdır) |

### Hazır Çalıştırılabilir Sürüm (Kurulumsuz Kullanım)

Kaynak koddan derleme yapmadan doğrudan kullanmak için **[Releases](https://github.com/HypeAITR/Forecord/releases/latest)** sayfasından hazır paketleri indirebilirsiniz:

- **`Forecord-v1.0.0-win-x64.zip` (Tam Paket):** Bağımsız (self-contained) `Forecord.exe` ve `ffmpeg.exe` dosyalarını bir arada içerir. Ek bir `.NET Runtime` veya `FFmpeg` kurulumu gerektirmez; arşivi çıkartıp `Forecord.exe` dosyasını çalıştırmanız yeterlidir.
- **`Forecord.exe` (Tek Dosyalı Uygulama):** Sisteminde `ffmpeg.exe` zaten `PATH` ortam değişkeninde tanımlı olan kullanıcılar için tek dosyalı çalıştırılabilir sürüm.

### Kaynak Koddan Kurulum ve Derleme

#### 1. Kaynak Kodun İndirilmesi
```powershell
git clone https://github.com/HypeAITR/Forecord.git
cd Forecord
```

#### 2. FFmpeg Bağımlılığının Tanımlanması
Resmi FFmpeg dağıtımlarından ([Gyan.dev](https://www.gyan.dev/ffmpeg/builds/) vb.) temin edilen `ffmpeg.exe` ikili dosyası aşağıdaki yöntemlerden biriyle tanımlanmalıdır:
- `ScreenRecorder/` proje dizinine (veya derleme çıktı dizinine) kopyalanması,
- Sistem `PATH` ortam değişkenine eklenmesi.

#### 3. Derleme ve Çalıştırma
```powershell
cd ScreenRecorder

dotnet restore
dotnet build -c Release
dotnet run -c Release
```

#### Bağımsız Çalıştırılabilir Dosya (Single-File Executable) Üretimi
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

### Varsayılan Klavye Kısayolları

Kısayol tanımları uygulama içerisindeki **Ayarlar** penceresinden değiştirilebilir.

| İşlev | Varsayılan Kısayol |
|---|---|
| Kaydı Başlat / Durdur | `Ctrl + Alt + R` |
| Duraklat / Devam Et | `Ctrl + Alt + P` |
| Mikrofon Aç / Kapat | `Ctrl + Alt + M` |
| Ekran Görüntüsü Al (Kayıt Sırasında) | `Ctrl + Alt + S` |

**Çizim Katmanı Kısayolları (Çizim modu aktifken):**

| İşlev | Kısayol |
|---|---|
| Kalem Aracı | `D` |
| Silgi Aracı | `E` |
| Son İşlemi Geri Al | `Ctrl + Z` |
| Tüm Çizimleri Temizle | `C` |
| Çizim Aracını Devre Dışı Bırak (Masaüstü Etkileşimine Dön) | `Esc` |

### Dizin Yapısı

```text
Forecord/
└── ScreenRecorder/
    ├── ScreenRecorder.csproj        # Proje yapılandırması ve paket bağımlılıkları
    ├── App.xaml / App.xaml.cs       # Uygulama yaşam döngüsü ve stil tanımları
    ├── MainWindow.xaml(.cs)         # Ana yönetim penceresi
    ├── Helpers/
    │   └── DesktopCapture.cs        # DXGI Desktop Duplication, dirty-rect ve imleç işleme
    ├── Models/                      # Veri modelleri ve durum tanımları
    ├── Services/
    │   ├── RecordingService.cs      # Kayıt akışı, VFR zamanlama ve segment yönetimi
    │   ├── EncoderService.cs        # FFmpeg süreç yönetimi, donanım kodlayıcı tespiti ve muxing
    │   ├── AudioService.cs          # WASAPI ses yakalama ve Named Pipe veri akışı
    │   ├── HotkeyService.cs         # Win32 global klavye kısayolu yönetimi
    │   ├── SettingsService.cs       # Kullanıcı yapılandırma yönetimi (%AppData%\Forecord)
    │   ├── FileService.cs           # Çıktı ve geçici dosya yönetimi
    │   ├── GpuEncoding/             # Doğrudan NVIDIA NVENC ve AMD AMF D3D11 kodlama katmanı
    │   └── Muxing/                  # H.264 Annex-B -> MP4 paketleme modülü (H264Mp4Muxer)
    ├── ViewModels/                  # MVVM görünüm modelleri
    ├── Views/                       # Kullanıcı arayüzü pencereleri ve çizim katmanı bileşenleri
    └── Effects/ & Shaders/          # WPF HLSL piksel shader bileşenleri
```

### Lisans

Bu proje **MIT Lisansı** kapsamında sunulmaktadır. Lisans koşulları için [`LICENSE`](LICENSE) dosyasına bakınız.

---

## English

### Overview

**Forecord** is an open-source desktop screen recording application for Windows 10 and Windows 11 built on `.NET 8` and `WPF`. It is designed to provide high-quality video and audio capture with minimal system overhead, serving as a lightweight alternative to complex broadcasting suites.

The application integrates GPU-based desktop capture (DXGI Desktop Duplication), hardware-accelerated video encoding, real-time audio-video muxing, and live on-screen annotation within a streamlined interface.

### Technical Features

- **Desktop Capture Architecture**
  - Low-latency frame acquisition via Direct3D 11 and the **DXGI Desktop Duplication API**.
  - **Variable Frame Rate (VFR)** pacing with duplicate-frame skipping and **Dirty-Rect / Move-Rect** partial readback optimization to minimize memory bandwidth during static screen periods.
  - Adaptive performance management that dynamically adjusts frame rate caps and encoder presets based on system load and power state.
- **Video Encoding and Muxing**
  - Automatic hardware encoder detection and fallback chain: **NVIDIA NVENC (`h264_nvenc`)**, **AMD AMF (`h264_amf`)**, **Intel QSV (`h264_qsv`)**, and software **CPU (`libx264`)**.
  - Direct D3D11 GPU texture encoding (`NvencGpuEncoder`, `AmfGpuEncoder`) and an integrated MP4 container muxer (`H264Mp4Muxer`).
  - Support for 30, 60, and 120 FPS targets alongside Native, 1080p, 1440p, and 4K output resolutions.
- **Audio Capture and Synchronization**
  - Simultaneous microphone and system audio (loopback) capture via **WASAPI**.
  - Real-time audio-video muxing over Named Pipes during recording to avoid post-recording transcoding delays.
  - On-the-fly audio source toggling with seamless segment transitions.
- **Recording Controls and On-Screen Annotation**
  - Floating control bar excluded from the recorded video stream via the Windows `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` API.
  - Full-screen transparent annotation overlay supporting pen, arrow, and eraser tools, with automatic click-through (`WS_EX_TRANSPARENT`) when no drawing tool is active.
  - On-demand (lazy) cursor-free PNG screenshot capture without per-frame memory copying overhead.
  - Pause and resume functionality with automatic segment concatenation into a single MP4 output file.

### System Requirements

| Component | Requirement |
|---|---|
| **Operating System** | Windows 10 (Version 2004 / Build 19041 or later) or Windows 11 (x64) |
| **.NET SDK (Source Build Only)** | [.NET 8.0 SDK (Windows x64)](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **FFmpeg** | `ffmpeg.exe` (bundled in the full release archive; required in the app directory or system `PATH` for standalone `.exe` usage) |

### Prebuilt Binaries (No Installation Required)

To run the application without building from source, download the prebuilt packages from the **[Releases](https://github.com/HypeAITR/Forecord/releases/latest)** page:

- **`Forecord-v1.0.0-win-x64.zip` (Full Bundle):** Contains the self-contained `Forecord.exe` executable and `ffmpeg.exe`. Requires no external `.NET Runtime` or `FFmpeg` installation; extract the archive and run `Forecord.exe`.
- **`Forecord.exe` (Standalone Single-File Executable):** Self-contained single-file executable for systems where `ffmpeg.exe` is already configured in the system `PATH`.

### Building from Source

#### 1. Clone the Repository
```powershell
git clone https://github.com/HypeAITR/Forecord.git
cd Forecord
```

#### 2. Configure FFmpeg
Obtain `ffmpeg.exe` from an official distribution (e.g., [Gyan.dev FFmpeg Builds](https://www.gyan.dev/ffmpeg/builds/)) and either:
- Copy `ffmpeg.exe` into the `ScreenRecorder/` project directory (or build output directory), or
- Add the directory containing `ffmpeg.exe` to your system `PATH` environment variable.

#### 3. Build and Run
```powershell
cd ScreenRecorder

dotnet restore
dotnet build -c Release
dotnet run -c Release
```

#### Publish as a Single-File Executable
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

### Default Keyboard Shortcuts

All global shortcuts can be customized through the **Settings** window.

| Action | Default Shortcut |
|---|---|
| Start / Stop Recording | `Ctrl + Alt + R` |
| Pause / Resume Recording | `Ctrl + Alt + P` |
| Toggle Microphone | `Ctrl + Alt + M` |
| Capture Screenshot (During Recording) | `Ctrl + Alt + S` |

**Annotation Overlay Shortcuts (When annotation toolbar is open):**

| Action | Shortcut |
|---|---|
| Pen Tool | `D` |
| Eraser Tool | `E` |
| Undo Last Action | `Ctrl + Z` |
| Clear All Annotations | `C` |
| Release Active Tool (Return to Desktop Interaction) | `Esc` |

### Project Structure

```text
Forecord/
└── ScreenRecorder/
    ├── ScreenRecorder.csproj        # Project configuration and NuGet dependencies
    ├── App.xaml / App.xaml.cs       # Application lifecycle and resource dictionaries
    ├── MainWindow.xaml(.cs)         # Main control window
    ├── Helpers/
    │   └── DesktopCapture.cs        # DXGI Desktop Duplication, dirty-rect, and cursor compositing
    ├── Models/                      # Data models and state definitions
    ├── Services/
    │   ├── RecordingService.cs      # Capture loop, VFR pacing, and segment orchestration
    │   ├── EncoderService.cs        # FFmpeg process management, hardware probing, and muxing
    │   ├── AudioService.cs          # WASAPI audio capture and Named Pipe streaming
    │   ├── HotkeyService.cs         # Win32 global hotkey registration
    │   ├── SettingsService.cs       # JSON configuration persistence (%AppData%\Forecord)
    │   ├── FileService.cs           # Output and temporary file management
    │   ├── GpuEncoding/             # Direct NVIDIA NVENC and AMD AMF D3D11 encoding layer
    │   └── Muxing/                  # H.264 Annex-B to MP4 muxer (H264Mp4Muxer)
    ├── ViewModels/                  # MVVM view models
    ├── Views/                       # UI windows, floating panel, and annotation overlay
    └── Effects/ & Shaders/          # WPF HLSL pixel shader components
```

### License

This project is licensed under the **MIT License**. See the [`LICENSE`](LICENSE) file for details.

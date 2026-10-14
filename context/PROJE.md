# Proje: amaç, durum, geçmiş

## Amaç

Windows 10/11 üzerinde çalışan, geliştirici, eğitmen ve sunum yapan kullanıcılar için tek tıkla başlayan, kayıt sırasında ön plandaki uygulamayı (IDE, tarayıcı, oyun, video) yavaşlatmayan ("zero-overhead" hedefli) açık kaynaklı ekran kaydedici (**Forecord**).

- **Başarı ölçütü:** Kayıt sırasında minimum CPU/bellek bant genişliği kullanımı, bozulmayan ses-görüntü senkronu, duraklat/devam et ve anlık ses aç/kapat geçişlerinde sıfır bozuk segment.
- **Kapsam dışı:** DRM / `WDA_EXCLUDEFROMCAPTURE` korumalı pencereleri yakalama (yüzen kontrol paneli bizzat bu API ile kayıttan gizlenir).

## Mevcut durum (2026-10-14)

| Alan | Durum |
|---|---|
| **Derleme ve Altyapı** | `.NET 8.0` (`net8.0-windows10.0.22621.0`), `x64`, WPF + SharpDX 4.2.0 + NAudio 2.2.1. `DebugCheck` derlemesi **0 uyarı, 0 hata** ile geçiyor (`ScreenRecorder/bin/DebugCheck/Forecord.dll`). |
| **Ekran Yakalama (`DesktopCapture.cs`)** | DXGI Desktop Duplication API. Tam kare veya `DirtyRect` / `MoveRect` kısmi CPU okuma (partial readback), yazılımsal imleç birleştirme (`CompositeCursor`/`RestoreCursor`) ve kopyasız GPU dokusu (`DesktopGpuFrame`) yakalama çalışıyor. |
| **VFR & Adaptif Performans (`RecordingService.cs`)** | Değişmeyen kareleri atlama (`EnableDebugDuplicateFrameSkip`), 250 ms keepalive, pil + dahili GPU tespiti (`PowerAwareCaptureProfile`) ve kodlayıcı geri basıncına göre dinamik FPS düşürme/yükseltme (`AdaptivePerformanceController`) aktif. |
| **Ekran Görüntüsü (Faz A)** | Lazy snapshot (`TrySaveScreenshotAsync`) devrede. Kayıt döngüsünde her karede `_latestFrame` kopyalama kaldırıldı; yalnızca kısayol (`Ctrl+Alt+S`) tetiklendiğinde imleçsiz PNG kaydediliyor. |
| **Ses ve Canlı Muxing (`AudioService.cs`, `EncoderService.cs`)** | WASAPI mikrofon + sistem sesi (loopback). 1 MB tamponlu Named Pipe (`\\.\pipe\Forecord_mic_*`, `Forecord_sys_*`) üzerinden FFmpeg'e gerçek zamanlı akış (`FFMPEG_LIVE_MUX`), `aresample=async=1000` ile saat kayması telafisi ve `ProbeSegmentAsync` doğrulaması çalışıyor. |
| **GPU-Direct Kodlama ve Yerleşik Muxer (`GpuEncoding/`, `Muxing/`)** | `NvencGpuEncoder` (`nvEncodeAPI64.dll`), `AmfGpuEncoder` (`amfrt64.dll`) ve saf C# `H264Mp4Muxer` (Annex-B -> AVCC MP4) mevcut. Şu an yalnızca video-only + native çözünürlükte (`EnableGpuDirectPipeline` veya `FORECORD_GPU_DIRECT_MUX=1` ile) devreye giriyor; ses veya ölçekleme açıksa otomatik FFmpeg yoluna düşüyor. |
| **Arayüz ve Çizim Katmanı (`Views/`)** | Ana pencere (`LayeredBackground` + HLSL `CursorDisplacement` shader), kayıtta görünmeyen `FloatingPanel`, tam ekran tıklama-geçirgen `DrawingOverlayWindow` (kalem, fosforlu kalem, şekil, ok, metin, silgi) çalışıyor. |

## Geçmiş (kronolojik)

1. **2026-05-12 – 2026-05-15:** Temel WPF arayüzü, DXGI yakalama, FFmpeg kodlama, global kısayollar, çizim katmanı (`DrawingOverlayWindow`) ve yüzen panel (`FloatingPanel`) geliştirildi.
2. **2026-05-17 – 2026-05-25:** Sıfır ek yük (Zero-Overhead) mimari iyileştirmeleri uygulandı:
   - Faz A: Lazy screenshot snapshot (`TrySaveScreenshotAsync`).
   - Faz 1 & 3: Canlı ses Named Pipe muxing (`EnableDebugLiveMux`), VFR duplicate-frame skip, dirty/move-rect kısmi okuma, adaptif FPS ve güç profili yönetimi.
   - Faz 2 (Deneysel): Doğrudan D3D11 -> NVENC/AMF kodlama (`Services/GpuEncoding/`) ve özel `H264Mp4Muxer` (`Services/Muxing/`) eklendi.
3. **2026-10-13:** Git deposu düzenlendi, `yolharitası.md` ve iki dilli (TR/EN) `README.md` belgeleri oluşturuldu.
4. **2026-10-14:** Proje tarandı, `DebugCheck` derlemesi doğrulandı, `AGENTS.md` + `context/` hafıza mimarisi kuruldu. İşlevi olmayan sarmalayıcı `SothelCaptureEngine` ve `RecordingCaptureMode` tamamen kaldırıldı.

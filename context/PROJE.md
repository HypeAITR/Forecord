# Proje: amaç, durum, geçmiş

## Amaç

Windows 10/11 üzerinde çalışan, geliştirici, eğitmen ve sunum yapan kullanıcılar için tek tıkla başlayan, kayıt sırasında ön plandaki uygulamayı (IDE, tarayıcı, oyun, video) yavaşlatmayan ("zero-overhead" hedefli) açık kaynaklı ekran kaydedici (**Forecord**).

- **Başarı ölçütü:** Kayıt sırasında minimum CPU/bellek bant genişliği kullanımı, bozulmayan ses-görüntü senkronu, duraklat/devam et ve anlık ses aç/kapat geçişlerinde sıfır bozuk segment.
- **Kapsam dışı:** DRM / `WDA_EXCLUDEFROMCAPTURE` korumalı pencereleri yakalama (yüzen kontrol paneli bizzat bu API ile kayıttan gizlenir).

## Mevcut durum (2026-10-14)

| Alan | Durum |
|---|---|
| **Derleme ve Altyapı** | `.NET 8.0` (`net8.0-windows10.0.22621.0`), `x64`, WPF + SharpDX 4.2.0 + NAudio 2.2.1. `DebugCheck` derlemesi ve `--self-test` (4 aşamalı donanım + uçtan uca kayıt testi) **0 uyarı, 0 hata** ile geçiyor (`ScreenRecorder/bin/DebugCheck/Forecord.exe`). |
| **Ekran Yakalama (`DesktopCapture.cs`)** | DXGI Desktop Duplication API. Tam kare veya `DirtyRect` / `MoveRect` kısmi CPU okuma, yazılımsal imleç birleştirme (`CompositeCursor`/`RestoreCursor`), kopyasız GPU dokusu (`DesktopGpuFrame`) yakalama ve alt-bölge GPU imleç çizimi (`CompositeCursorOnGpuTexture`) çalışıyor. |
| **GPU-Direct Kodlama ve Yerleşik Muxer (`GpuEncoding/`, `Muxing/`)** | **Varsayılan olarak aktif (`EnableGpuDirectPipeline = true`).** `NvencGpuEncoder` (`nvEncodeAPI64.dll`), `AmfGpuEncoder` (`amfrt64.dll`), `D3D11TextureScaler` (GPU üzerinde 720p/1080p/1440p/4K ölçekleme) ve saf C# `H264Mp4Muxer` (Annex-B -> AVCC MP4 + yerel segment birleştirme `TryConcatenateSegments`) tam kapasite çalışıyor. Ses açıkken WASAPI ile paralel kayıt alıp kayıt sonunda `-c:v copy` ile yeniden kodlamasız birleştiriyor. |
| **Kayıt Motoru & Tanılama (`Services/Recording/`, `Diagnostics/`)** | `RecordingService.cs` 540 satıra indirildi; alt bileşenler (`AdaptivePerformanceController`, `PowerAwareCaptureProfile`, `ScreenshotCaptureCoordinator`, `RecordingTelemetry`, `GpuDirectSegmentRunner`) ayrıştırıldı. Hatalar `RecordingDiagnostics` ile `%LocalAppData%\Forecord\logs\` altına yazılıyor; `PipelineSelfTest` (`--self-test`) ile doğrulanıyor. |
| **Ses ve Canlı Muxing (`AudioService.cs`, `EncoderService.cs`)** | WASAPI mikrofon + sistem sesi (loopback). GPU-Direct desteklenmeyen sistemlerde otomatik olarak 1 MB tamponlu Named Pipe (`FFMPEG_LIVE_MUX`) yoluna düşüyor. |
| **Arayüz ve Çizim Katmanı (`Views/`)** | Ana pencere (`LayeredBackground` + HLSL `CursorDisplacement` shader), Ayarlar'da Zero-Overhead GPU-Direct seçeneği, kayıtta görünmeyen `FloatingPanel`, tam ekran tıklama-geçirgen `DrawingOverlayWindow` çalışıyor. |

## Geçmiş (kronolojik)

1. **2026-05-12 – 2026-05-15:** Temel WPF arayüzü, DXGI yakalama, FFmpeg kodlama, global kısayollar, çizim katmanı (`DrawingOverlayWindow`) ve yüzen panel (`FloatingPanel`) geliştirildi.
2. **2026-05-17 – 2026-05-25:** Sıfır ek yük (Zero-Overhead) mimari iyileştirmeleri uygulandı:
   - Faz A: Lazy screenshot snapshot (`TrySaveScreenshotAsync`).
   - Faz 1 & 3: Canlı ses Named Pipe muxing (`EnableDebugLiveMux`), VFR duplicate-frame skip, dirty/move-rect kısmi okuma, adaptif FPS ve güç profili yönetimi.
   - Faz 2 (Deneysel): Doğrudan D3D11 -> NVENC/AMF kodlama (`Services/GpuEncoding/`) ve özel `H264Mp4Muxer` (`Services/Muxing/`) eklendi.
3. **2026-10-13:** Git deposu düzenlendi, `yolharitası.md` ve iki dilli (TR/EN) `README.md` belgeleri oluşturuldu.
4. **2026-10-14:** Proje tarandı, `DebugCheck` derlemesi doğrulandı, `AGENTS.md` + `context/` hafıza mimarisi kuruldu. İşlevi olmayan sarmalayıcı `SothelCaptureEngine` ve `RecordingCaptureMode` tamamen kaldırıldı.

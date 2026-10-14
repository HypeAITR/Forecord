# Yol haritası

Öncelik sırasıyla. Tamamlananı işaretle (tarihiyle), yenisini ekle.

## Tamamlananlar
- [x] **Faz A — Lazy Screenshot Snapshot (2026-05-17):** `_latestFrame` her karede kopyalama kaldırıldı, `TrySaveScreenshotAsync` ile isteğe bağlı imleçsiz PNG kaydı eklendi.
- [x] **Faz 1 — Canlı Ses Muxing ve VFR / Partial Readback (2026-05-20):** Named Pipe üzerinden canlı ses muxing (`FFMPEG_LIVE_MUX`), DXGI `LastPresentTime` tabanlı duplicate-frame skip ve `DirtyRect`/`MoveRect` kısmi CPU okuma tamamlandı.
- [x] **Faz 2A/2B — GPU-Direct Kodlama ve Yerleşik MP4 Muxer Prototipi (2026-05-25):** `NvencGpuEncoder`, `AmfGpuEncoder` ve `H264Mp4Muxer` yazıldı; video-only ve native çözünürlükte çalışıyor.
- [x] **Faz 3 — Adaptif FPS ve Güç Profili (2026-05-25):** `AdaptivePerformanceController` ve `PowerAwareCaptureProfile` devreye alındı.
- [x] **Proje Belgeleri ve Hafıza Altyapısı (2026-10-14):** `README.md`, `yolharitası.md`, `AGENTS.md` ve `context/` dosyaları oluşturuldu, `DebugCheck` derlemesi doğrulandı.

## Şimdi (Sıradaki Potansiyel İşler)
Ayrıntılı teknik plan `context/GPU-DIRECT-PLAN.md` dosyasındadır:
- [x] **Faz 0 (2026-10-14):** `RecordingService.cs` (1450+ satırdan ~540 satıra) Modülerleştirme + Hata Tanılama (`RecordingDiagnostics`) ve Öz-Test (`PipelineSelfTest` / `--self-test`) Altyapısı. (Testler sırasında AMD `AmfGpuEncoder` içindeki double-free `0xc0000409` çökmesi yakalanıp düzeltildi!)
- [x] **Faz 1 (2026-10-14):** GPU Kodlayıcıların (`NvencGpuEncoder`, `AmfGpuEncoder`) Sıfır-Tahsis (Zero-Allocation) ve Kalite/GOP Optimizasyonu. (Her karede doku/bitstream yaratma kaldırıldı; canlı GPU -> `H264Mp4Muxer` -> FFmpeg decode doğrulaması geçti.)
- [x] **Faz 2 (2026-10-14):** GPU-Direct Döngüsünde Fare İmleci (`CompositeCursorOnGpuTexture`), Lazy Screenshot (`ReadbackFullTexture`) ve VFR Keepalive/Telemetri Desteği.
- [x] **Faz 3 (2026-10-14):** GPU-Direct Çoklu Segment (`H264Mp4Muxer.TryConcatenateSegments`) ve Çözünürlük Ölçekleme (`D3D11TextureScaler`) Desteği.
- [x] **Faz 4 (2026-10-14):** GPU-Direct + Ses Kaydı Entegrasyonu (Mikrofon/Sistem sesi açıkken de video sıfır yükle GPU-Direct üzerinden kaydedilir, kayıt sonunda `-c:v copy` ile yeniden kodlamasız birleştirilir).
- [ ] **Faz 5:** Ayarlar Penceresi (`EnableGpuDirectPipeline`) Entegrasyonu ve Otomatik Fallback.

## Açık sorular
- GPU-Direct (`EnableGpuDirectPipeline`) kullanıcı arayüzünde (Ayarlar penceresinde) ne zaman varsayılan veya görünür seçenek haline getirilecek?
- Çoklu monitör (sanal masaüstü birleştirme) veya pencere bazlı yakalama (`Windows.Graphics.Capture`) yol haritasına eklenecek mi?

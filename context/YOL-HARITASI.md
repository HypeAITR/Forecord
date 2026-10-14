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
- [ ] **Faz 2:** GPU-Direct Döngüsünde Fare İmleci (GPU Cursor) ve Ekran Görüntüsü (Lazy Screenshot) Desteği.
- [ ] **Faz 3:** GPU-Direct Çoklu Segment (Pause/Resume) ve Çözünürlük Ölçekleme Desteği.
- [ ] **Faz 4:** `H264Mp4Muxer` Ses (AAC Track) Entegrasyonu (ses açıkken de FFmpeg'siz kayıt).
- [ ] **Faz 5:** Ayarlar Penceresi (`EnableGpuDirectPipeline`) Entegrasyonu ve Otomatik Fallback.

## Açık sorular
- GPU-Direct (`EnableGpuDirectPipeline`) kullanıcı arayüzünde (Ayarlar penceresinde) ne zaman varsayılan veya görünür seçenek haline getirilecek?
- Çoklu monitör (sanal masaüstü birleştirme) veya pencere bazlı yakalama (`Windows.Graphics.Capture`) yol haritasına eklenecek mi?

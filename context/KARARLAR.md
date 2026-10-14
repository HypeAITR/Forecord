# Kararlar

Her karar: ne, neden, kim karar verdi ve sonraki modelin bilmesi gereken gizli ayrıntılar. Yeni kararları sona ekle (K-XX). Kullanıcı açıkça istemedikçe kapanmış kararları yeniden tartışma.

---

### K-01 · Derleme Çıktı Yolu: `ScreenRecorder/bin/DebugCheck` (2026-05-17)
- **Neden:** Geliştirme sırasında yapılan denemelerin kararlı `Release` çıktılarını veya standart `bin/Debug` klasörünü bozmaması için tüm doğrulama derlemeleri `-c Debug -o ./ScreenRecorder/bin/DebugCheck` hedefinde toplanır.
- **Kim:** Kullanıcı (`PROMPT_phase_A_codex.md` ve `yolharitası.md`).
- **Gizli ayrıntı:** `ScreenRecorder/ffmpeg.exe` (yaklaşık 101 MB) kaynak klasörde durur; uygulama çalışırken `EncoderService.FindFFmpeg()` önce `Environment.ProcessPath` yanına, sonra sistem `PATH`'ine bakar.

### K-02 · Lazy Screenshot Mekanizması (2026-05-17)
- **Neden:** Her karede `Buffer.BlockCopy` ile `_latestFrame` yedeklemek 4K 60 FPS'de saniyede ~2 GB CPU bellek bant genişliği harcıyordu. Bunun yerine yalnızca kullanıcı ekran görüntüsü istediğinde (`_screenshotPending = true`) tek bir kare `TaskCompletionSource` ile kopyalanır.
- **Kim:** Kullanıcı / Model (`PROMPT_phase_A_codex.md`).
- **Gizli ayrıntı:** Ekran görüntüsü kopyası `TryWriteFrameToPipe` içindeki `cap.CompositeCursor(buffer)` çağrısından **önce** alınır; böylece alınan PNG ekran görüntülerinde fare imleci yer almaz.

### K-03 · Canlı Ses Muxing (`FFMPEG_LIVE_MUX`) ve Segment Birleştirme (2026-05-20)
- **Neden:** Sesleri ayrı `.wav` dosyalarına kaydedip kayıt sonunda video ile birleştirmek (post-mux) uzun kayıtlarda dakikalarca "Dosya hazırlanıyor…" beklemesine yol açıyordu. Named Pipe (`\\.\pipe\Forecord_mic_*` ve `Forecord_sys_*`) ile sesler kayıt anında FFmpeg'e akıtılarak her segment doğrudan hazır `.mp4` olarak üretilir.
- **Kim:** Kullanıcı / Model (`RecordingService.cs`, `AudioService.cs`).
- **Gizli ayrıntı:** Mikrofon veya sistem sesi kayıt ortasında açılıp kapatıldığında `MainViewModel.RestartSegmentIfRecording()` aktif segmenti sonlandırıp yeni ses yapılandırmasıyla yeni segment başlatır. Segmentler arasında ses kanalı sayısı değişebileceği için `EncoderService.ConcatenateMuxedSegmentsAsync` gerekli olduğunda sessiz ses kaynağı (`anullsrc`) ile uyumluluğu korur.

### K-04 · Doğrudan GPU Kodlama (NVENC / AMF) ve `H264Mp4Muxer` Sınırları (2026-05-25)
- **Neden:** CPU geri okumasını (staging readback) ve `ffmpeg.exe` borusunu tamamen devre dışı bırakmak için D3D11 dokularını doğrudan `nvEncodeAPI64.dll` veya `amfrt64.dll` ile H.264'e kodlayıp `H264Mp4Muxer` ile MP4'e yazan katman eklendi.
- **Kim:** Kullanıcı / Model (`Services/GpuEncoding/`, `Services/Muxing/`).
- **Gizli ayrıntı:** `TryBeginDebugGpuDirectMuxSegment` şu an yalnızca **ses kapalıyken** (`!micEnabled && !sysEnabled` veya `FORECORD_GPU_DIRECT_MUX=1` zorlamasıyla sessiz) ve **çıkış çözünürlüğü Native iken** (`_outW == _screenW && _outH == _screenH`) devreye girer; aksi halde otomatik olarak FFmpeg boru hattına geri döner. Ayrıca GPU-Direct modunda donanım imleci henüz GPU dokusu üzerine çizilmemektedir.

### K-05 · Yüzen Panel Gizleme ve DRM Kapsam Sınırı (2026-05-17)
- **Neden:** `FloatingPanel` kayıt sırasında ekranda görünür kalmalı ama kaydedilen videoda çıkmamalıdır; bunun için Win32 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` kullanılır.
- **Kim:** Kullanıcı (`PROMPT_zero_overhead.md`).
- **Gizli ayrıntı:** DXGI Desktop Duplication API doğası gereği `WDA_EXCLUDEFROMCAPTURE` veya DRM korumalı pencereleri siyah yakalar. Projede hiçbir şekilde DRM/koruma bypass kodu yazılmaz.

### K-06 · `SothelCaptureEngine` ve `RecordingCaptureMode` Sarmalayıcısının Kaldırılması (2026-10-14)
- **Neden:** `SothelCaptureEngine` gerçek bir yakalama yapmıyor; yalnızca `DesktopCapture.TryGetFrame` ve `TryAcquireGpuFrame` çağrılarını sarmalayıp zaten `RecordingService` içinde tutulan sayaçları tekrar sayıyor ve Ayarlar penceresinde gereksiz bir "Mod" seçimi gösteriyordu.
- **Kim:** Kullanıcı istedi, doğrulandıktan sonra kaldırıldı.
- **Gizli ayrıntı:** `SothelCaptureEngine.cs` ve `RecordingCaptureMode.cs` silindi; `RecordingService`, `AppSettings`, `SettingsViewModel` ve `SettingsWindow.xaml` doğrudan `DesktopCapture` çağrılarıyla sadeleştirildi.

# Forecord — Sıfır Beklemeli (Zero-Wait Stop) Yerleşik Ses + Video MP4 Muxer Planı

## 1. Sorunun Kök Nedeni (15 Dakikalık Kayıtta Neden Bekletti?)

1. **`H264Mp4Muxer` İki Kere Dosya Yazıyordu (`.mdat.tmp` Kopyası):**
   - Kayıt boyunca tüm H.264 paketleri önce `seg0000.mp4.mdat.tmp` dosyasına yazılıyordu.
   - Segment biterken (`Finish()`) yüzlerce megabaytlık `.mdat.tmp` dosyası baştan sona okunup `.mp4` dosyasına kopyalanıyordu (`_mdat.CopyTo(output)`).
2. **Ses Kayıt Sırasında Değil, Kayıt Bittikten Sonra (`StopAsync`) FFmpeg ile Kodlanıyordu:**
   - GPU-Direct modunda video MP4 olarak yazılırken ses sıkıştırılmamış `.wav` olarak yazılıyordu.
   - `StopAsync` çağrıldığında FFmpeg başlatılıp 15 dakikalık tüm `.wav` dosyaları baştan sona okunuyor, `aresample` + `amix` filtresinden geçirilip sıfırdan AAC'ye kodlanıyor ve `-movflags +faststart` yüzünden tüm MP4 dosyası diskte bir kez daha kopyalanıyordu.

---

## 2. Saat Kayması (Audio/Video Clock Drift) ve Senkronizasyon Çözümü

- Video kareleri CPU `Stopwatch` (QPC) saatine göre zaman damgası (`PTS`) alır.
- Ses kartları (WASAPI mikrofon ve sistem loopback) kendi kristal osilatörlerini kullanır ve zaman zaman sessizlik/gecikme boşlukları (gap) bırakır.
- **Çözüm (`WallclockAudioMixer`):**
  - Ses karıştırıcı, video ile **aynı `Stopwatch` saatini** paylaşır.
  - `48000 Hz` stereo akışta her 1 AAC çerçevesi tam olarak `1024` örnek (`21.3333 ms`) sürer.
  - Mikrofon ve sistem sesinden gelen PCM örnekleri `Stopwatch` zamanına göre halka tampona (ring buffer) yerleştirilir; cihaz gecikirse veya sessiz kalırsa eksik örnek kadar sıfır (sessizlik) doldurulur, hızlı koşarsa hizalanır. Böylece 15 dakikalık kayıtta bile ses ve video süresi milisaniyesi milisaniyesine kilitli kalır.

---

## 3. Uygulama Fazları (Her Faz Ayrı Test + Ayrı Commit)

- [x] **Faz A (2026-10-14) — `H264Mp4Muxer` Sıfır-Kopya (`.mdat.tmp` Kaldırma):**
  Doğrudan hedef `.mp4` dosyasını aç, ilk 48 byte'ı (`ftyp` 32B + `mdat` 64-bit header 16B) rezerve et, paketleri doğrudan `.mp4`'e yaz, `Finish()` anında kopyalama yapmadan sona `moov` yazıp ilk 48 byte'ı güncelle (`Finish()` süresi **0 ms** olarak ölçüldü).
- [ ] **Faz B — `H264Mp4Muxer` Yerleşik AAC Ses Track'i (`mp4a` / `esds`) ve Çoklu Segment Birleştirme:**
  `H264Mp4Muxer` içine ikinci `trak` (`soun` / `mp4a` / `esds` AudioSpecificConfig) ve `TryConcatenateSegments` içine ses birleştirme desteği ekle.
- [ ] **Faz C — Saat-Kilitli Canlı Ses Karıştırıcı ve Windows Yerleşik AAC Kodlayıcı (`LiveAacAudioPipeline`):**
  WASAPI mikrofon + sistem sesini canlı karıştırıp Windows Media Foundation AAC MFT ile kayıt esnasında anlık AAC paketlerine çeviren ve senkronizasyon kaymasını (drift) önleyen boru hattı.
- [ ] **Faz D — `GpuDirectSegmentRunner` & `RecordingService` Bağlantısı (FFmpeg'siz Anında Kayıt Bitişi):**
  Kayıt sırasında video + sesi doğrudan `H264Mp4Muxer` içine yaz, `StopAsync` çağrıldığında FFmpeg çalıştırmadan anında (`< 250 ms`) dosyayı teslim et.

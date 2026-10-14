# Forecord — GPU-Direct & Yerleşik MP4 Muxer Tamamlama Planı

Bu belge, Forecord'un `ffmpeg.exe` boru hattına ihtiyaç duymadan doğrudan ekran kartı üzerinde kodlama (`NvencGpuEncoder` / `AmfGpuEncoder`) ve yerleşik paketleme (`H264Mp4Muxer`) yapan **GPU-Direct ("Sıfır Ek Yük")** mimarisinin analizini ve adım adım tamamlama planını içerir.

---

## 1. Kritik Soru: Bu Sistem Gerçekten FFmpeg'den Daha mı Hafif Olacak?

**Kısa Cevap:** **Evet, hem de dramatik ölçüde (%80–90 daha az CPU ve RAM bant genişliği).** Ancak mevcut prototip kodda her karede yapılan birkaç kaynak ayırma (allocation) hatası var; onlar düzeltildiğinde gerçek "ShadowPlay / ReLive" seviyesine inecek.

### Neden Çok Daha Hafif? (Veri Yolu Karşılaştırması)

Örnek senaryo: **4K (3840×2160) @ 60 FPS** (1 kare = **33.17 MB** ham BGRA veri, saniyede **~2 GB** veri).

#### Mevcut FFmpeg Yolu (`CaptureLoop` → `ffmpeg.exe`)
Her bir karede veri şu duraklardan geçiyor:
1. **GPU → Staging Texture Kopyası:** VRAM içinde kopya.
2. **VRAM → Sistem RAM'i (PCIe Readback):** `MapSubresource` + `Buffer.MemoryCopy` ile kare GPU'dan CPU RAM'ine çekilir (**~2 GB/sn PCIe + RAM yazma**).
3. **CPU İmleç Çizimi:** CPU üzerinde `buffer` pikselleri değiştirilir ve geri yüklenir.
4. **C# → OS Kernel → FFmpeg Stdin Borusu (`pipe.Write`):** 33 MB veri her karede işletim sistemi borusuna yazılır, `ffmpeg.exe` süreci tarafından tekrar okunur (**~2 GB/sn kullanıcı-çekirdek kopyası**).
5. **FFmpeg CPU Renk Dönüşümü (`sws_scale`):** FFmpeg aldığı ham `bgra` pikselleri CPU üzerinde `yuv420p`'ye çevirir (**yoğun CPU yükü**).
6. **CPU → GPU Geri Yükleme:** FFmpeg dönüştürdüğü kareyi kodlamak için PCIe üzerinden **tekrar ekran kartına** (NVENC/AMF'ye) geri gönderir.

> **Sonuç:** Görüntü zaten ekran kartındayken, sırf `ffmpeg.exe`'ye boruyla vermek için **GPU → CPU → Kernel → FFmpeg CPU → GPU** şeklinde dev bir U-dönüşü yapıyor.

#### Kendi Yazdığımız GPU-Direct Yolu (`CaptureGpuDirectMuxLoop` → `IGpuEncoder` → `H264Mp4Muxer`)
1. **DXGI Capture:** Kare GPU'da yakalanır (`DesktopGpuFrame`). **CPU'ya hiç inmez.**
2. **GPU İçi Renk Dönüşümü + Donanım Kodlama:** Doku doğrudan NVENC (`NvEncRegisterResource`) veya AMF'ye (`CreateSurfaceFromDX11Native`) verilir. BGRA → YUV dönüşümünü ekran kartının kendi donanımı (ASIC) sıfır CPU yüküyle yapar.
3. **CPU'ya İnen Tek Şey:** Sadece sıkıştırılmış H.264 paketleri! (Saniyede 2.000 MB ham piksel yerine saniyede sadece **1–3 MB** bit akışı).
4. **`H264Mp4Muxer`:** Saniyede birkaç megabaytlık bu küçük paketleri doğrudan `.mp4` dosyasına yazar.

### ⚠️ Ama Dikkat: Mevcut Prototipte Düzeltilmesi Gereken 3 Darboğaz
Kodu incelediğimde, GPU-Direct katmanının şu an "smoke test" (ilk çalıştırma) şeklinde yazıldığını ve şu 3 şeyin her karede gereksiz yük bindirdiğini gördüm:
1. **`NvencGpuEncoder.EncodeTexture` içinde her karede kaynak yaratıp yıkma:** Şu an her karede `CreateBitstreamBuffer`, `RegisterD3D11Texture`, `UnregisterResource` ve `DestroyBitstreamBuffer` çağrılıyor. Saniyede 60 kez NVENC kayıt/silme yapmak sürücüyü yorar. Sabit 1 adet giriş dokusu (`_inputTexture`) ve 1 adet bitstream tamponu `Initialize()` içinde **bir kez** oluşturulup her karede sadece `CopyResource` + `Map/Encode/Unmap` yapılmalı.
2. **`AmfGpuEncoder.EncodeTexture` içinde her karede `new Texture2D` yaratma:** `CreateSharedGpuCopy` her karede yeni bir D3D11 dokusu yaratıp `Dispose` ediyor. Bu da VRAM'de parçalanma yapar. Tek bir paylaşımlı doku `Initialize()`'da ayrılıp yeniden kullanılmalı.
3. **`NvencNative.InitializeH264Encoder` kalite ve GOP ayarları:** Şu an `PresetGuid` ve `EncodeConfig` detayı verilmediği için NVENC varsayılan düşük bitrate/varsayılan GOP ile kodluyor; `Quality` ("Düşük"/"Orta"/"Yüksek") ve `FPS * 2` keyframe aralığı (GOP) yapılandırması bağlanmalı.

Bu 3 nokta Faz 1'de düzeltildiğinde GPU-Direct yolu **gerçek anlamda sıfıra yakın CPU (%0.5 - %1.5)** tüketecektir.

---

## 2. Faz Faz Uygulama Planı

İşi küçük, her adımı tek başına derlenip doğrulanabilir **6 faza** böldük. Her fazda gereksiz karmaşıklığa kaçmadan, tam gerektiği kadar **hata teşhis (tanılama) ve doğrulama testi** yer alır.

---

### Faz 0: `RecordingService.cs` Modülerleştirme + Hata Tanılama & Öz-Test (Self-Test) Altyapısı
**Sorun:** `RecordingService.cs` şu an **1.450+ satırlık** dev bir dosya. İçinde segment yönetimi, FFmpeg döngüsü, GPU-Direct döngüsü, adaptif FPS kontrolcüsü (`AdaptivePerformanceController`), pil/GPU güç profili (`PowerAwareCaptureProfile`), ekran görüntüsü yöneticisi ve telemetri iç içe duruyor. Ayrıca bir kayıt veya GPU kodlayıcı başarısız olduğunda hatanın hangi adımda (DXGI, NVENC, AMF, Muxer, FFmpeg) çıktığını net gösteren merkezi bir tanılama/test yapısı yok.

**Hedef:** Yeni özellik eklemeden önce kayıt motorunu temiz parçalara ayırmak ve abartısız, doğrudan hataları yakalayan bir tanılama + test altyapısı kurmak.

- **Adım 0.1 (`RecordingService` Bölünmesi — `Services/Recording/`):**
  1. `Services/Recording/AdaptivePerformanceController.cs` → Geri basınç (backpressure) ve dinamik FPS düşürme/yükseltme mantığı.
  2. `Services/Recording/PowerAwareCaptureProfile.cs` → Pil ve dahili GPU algılama profili.
  3. `Services/Recording/ScreenshotCaptureCoordinator.cs` → Lazy screenshot (`TaskCompletionSource`) ve PNG yazma mantığı.
  4. `Services/Recording/RecordingTelemetry.cs` → `#if DEBUG` altında dağılmış 16 adet `Interlocked` sayacın tek ve temiz bir sınıfta toplanması.
  5. `Services/Recording/GpuDirectSegmentRunner.cs` → GPU-Direct probe, kodlayıcı seçimi (`NVENC`/`AMF`) ve `CaptureGpuDirectMuxLoop` akışı.
  6. `Services/RecordingService.cs` → Yalnızca ana durum makinesi (`Start`, `Pause`, `Resume`, `Stop`, `Discard`) ve segment orkestrasyonu (~350-400 satıra inecek).
- **Adım 0.2 (Hata Raporlama ve Tanılama — `Services/Diagnostics/RecordingDiagnostics.cs`):**
  - Kayıt veya GPU-Direct başlatılırken/biterken oluşan hataları sessizce yutmak yerine, aşama bilgisiyle (`Stage`: `CaptureInit`, `GpuProbe`, `EncoderInit`, `EncodeFrame`, `MuxFinish`, `AudioPipe`, `SegmentConcat`) birlikte `%LocalAppData%\Forecord\logs\last_error.log` (veya temp klasörü) içine anlaşılır özet olarak yazan hafif tanılama yardımcısı.
- **Adım 0.3 (Gerektiği Kadar Öz-Test / Smoke Test — `Services/Diagnostics/PipelineSelfTest.cs`):**
  - Harici ağır test framework'leri kurmadan, doğrudan uygulama içinden (komut satırı `--self-test` argümanıyla veya debug çağrısıyla) çalışan hızlı doğrulama testleri:
    1. `TestMp4Muxer()`: Yapay SPS/PPS + IDR NAL paketiyle `H264Mp4Muxer`'ın geçerli MP4 üretip üretmediğini test eder.
    2. `TestDesktopCapture()`: DXGI üzerinden 1 adet CPU karesi ve 1 adet GPU karesi (`DesktopGpuFrame`) yakalanabildiğini doğrular.
    3. `TestGpuEncoder()`: Mevcut ekran kartında (NVENC veya AMF) 1 kare kodlayıp anlamlı hata/başarı raporu döner.
- **Doğrulama:** `DebugCheck` derlemesi (0 uyarı, 0 hata) ve `--self-test` ile tüm temel bileşenlerin geçtiğinin görülmesi.

---

### Faz 1: GPU Kodlayıcıların Sıfır-Tahsis (Zero-Allocation) ve Kalite Optimizasyonu
**Hedef:** NVENC ve AMF kodlayıcıların kayıt sırasında her karede bellek/kaynak ayırmasını bitirmek ve görüntü kalitesini FFmpeg yoluyla eşitlemek.
- **Adım 1.1 (`NvencGpuEncoder` & `NvencNative`):**
  - `Initialize` sırasında 1 adet `B8G8R8A8_UNorm` D3D11 giriş dokusu (`_inputTexture`), bunun `NvEncRegisterResource` kaydı ve 1 adet `NvencBitstreamBuffer` oluştur.
  - `EncodeTexture` içinde yalnızca `device.ImmediateContext.CopyResource(texture, _inputTexture)` yap, kayıtlı kaynağı map'le, kodla ve unmap et (her karede register/unregister/create/destroy kalkacak).
  - `GpuEncoderSettings.Quality` ("Düşük" / "Orta" / "Yüksek") ve FPS değerlerine göre NVENC Constant QP / VBR ve GOP (`IdrPeriod = fps * 2`, B-frame = 0) ayarlarını uygula.
  - `FinishAsync` içinde NVENC EOS (End-of-Stream) flush işlemini ekle.
- **Adım 1.2 (`AmfGpuEncoder`):**
  - `CreateSharedGpuCopy` içindeki her karede `new Texture2D` tahsisini kaldır; `Initialize` sırasında tek bir paylaşımlı `_sharedTexture` oluşturup her karede `CopyResource` ile üzerine yaz.
- **Doğrulama:** `DebugCheck` derlemesi (0 uyarı, 0 hata).

---

### Faz 2: GPU-Direct Döngüsünde Fare İmleci (Cursor) ve Ekran Görüntüsü (Screenshot)
**Hedef:** GPU-Direct ile kaydedilen videolarda fare imlecinin görünmesini ve kayıt sırasında `Ctrl+Alt+S` ile ekran görüntüsü alma özelliğinin çalışmasını sağlamak.
- **Adım 2.1 (GPU Üzerinde İmleç Çizimi - `DesktopCapture`):**
  - GPU-Direct yolunda kare CPU'ya inmediği için `CompositeCursor(byte[])` çalışmaz.
  - Kodlayıcının kullandığı hedef D3D11 dokusu (`_inputTexture`) üzerine, yalnızca imleç görünür olduğunda küçük imleç bölgesini (ör. 32×32 piksel) `UpdateSubresource` veya küçük bir staging/blend işlemiyle GPU tarafında yazan hafif bir metot (`CompositeCursorOnGpuTexture`) ekle. (Böylece 4K ekranın tamamını CPU'ya çekmeden sadece 32×32 piksellik imleç güncellenir).
- **Adım 2.2 (GPU-Direct Lazy Screenshot):**
  - `CaptureGpuDirectMuxLoop` içinde `_screenshotPending` kontrolü ekle; yalnızca kullanıcı ekran görüntüsü istediği o tek karede `gpuFrame.Texture`'ı `_stagingTex` üzerinden okuyup `TryCompletePendingScreenshot` ile imleçsiz PNG olarak kaydet.
- **Adım 2.3 (VFR Keepalive ve Telemetri):**
  - `CaptureGpuDirectMuxLoop` içine 250 ms'lik maksimum boşta kalma (keepalive) süresi ve `#if DEBUG` telemetri sayaçlarını (`_acquiredFrames`, `_encodedFrames`, `_skippedIdleFrames`) bağla.
- **Doğrulama:** `DebugCheck` derlemesi; GPU-Direct kayıtta imlecin görünmesi ve ekran görüntüsü kısayolunun PNG üretmesi.

---

### Faz 3: Çoklu Segment (Duraklat / Devam Et) ve Ölçekleme Desteği
**Hedef:** GPU-Direct modunda kaydı duraklatıp devam ettirmenin (Pause/Resume) ve çözünürlük ölçeklemenin (1080p/1440p/4K) sorunsuz çalışması.
- **Adım 3.1 (GPU Ölçekleme - Scaling):**
  - Kullanıcı `Native` dışında bir çözünürlük seçtiğinde (ör. 4K ekranda 1080p kayıt), D3D11 VideoProcessor veya Shader/RenderTarget blit ile GPU dokusunu kodlayıcıya vermeden önce `_outW x _outH` boyutuna GPU üzerinde küçült.
- **Adım 3.2 (Segment Birleştirme Doğrulaması):**
  - `StopAsync` içinde tek segment varsa doğrudan kopyalama (`File.Copy`), birden fazla segment varsa (Pause/Resume yapıldıysa) `H264Mp4Muxer` çıktılarının kayıpsız birleştirilmesi.
- **Doğrulama:** GPU-Direct modunda Pause → Resume → Stop akışının tek ve sağlam bir MP4 üretmesi.

---

### Faz 4: GPU-Direct + Ses Kaydı Entegrasyonu (`H264Mp4Muxer` Ses Desteği)
**Hedef:** Mikrofon ve sistem sesi açıkken de FFmpeg video kodlamasına geri dönmeden GPU-Direct modunda kayıt alabilmek.
- **Adım 4.1 (Windows Media Foundation AAC Kodlayıcı + MP4 Audio Track):**
  - Windows içinde yerleşik gelen Media Foundation AAC kodlayıcısını (veya `H264Mp4Muxer` içine ikinci bir `trak` olarak `mp4a` / `esds` AAC ses kutularını) ekleyerek `AudioService`'den gelen PCM ses paketlerini doğrudan `H264Mp4Muxer` içine yazmak; **veya**
  - Alternatif hibrit yol: Video GPU-Direct + `H264Mp4Muxer` ile sıfır yükle kaydedilirken, sesin hafif biçimde muxer'a entegre edilmesi. (Mimari açıdan en temiz ve `ffmpeg.exe`'den tamamen bağımsız olan yol: `H264Mp4Muxer` içine AAC `trak` eklemek).
- **Adım 4.2:** `TryBeginDebugGpuDirectMuxSegment` içindeki `if (micEnabled || sysEnabled) return false;` engelini kaldırmak.
- **Doğrulama:** Mikrofon + sistem sesi açıkken GPU-Direct ile sesli MP4 kaydı.

---

### Faz 5: Arayüz (Ayarlar) Entegrasyonu ve Otomatik Geri Dönüş (Fallback)
**Hedef:** Özelliği gizli ortam değişkeninden (`FORECORD_GPU_DIRECT_MUX`) çıkarıp kullanıcıya sunmak.
- **Adım 5.1 (`SettingsWindow.xaml` & `SettingsViewModel.cs`):**
  - Ayarlar → Performans kartına **"Doğrudan GPU Kodlama (Zero-Overhead Motoru)"** (`EnableGpuDirectPipeline`) seçeneğini eklemek ve varsayılan olarak desteklenen kartlarda (NVIDIA / AMD) aktif etmek.
- **Adım 5.2 (Kusursuz Fallback):**
  - Sistemde NVENC veya AMF yoksa (ör. sadece Intel iGPU veya eski kart varsa) ya da başlatma sırasında bir sürücü hatası olursa, kullanıcıya hata göstermeden sessizce mevcut FFmpeg yoluna (`FFMPEG_LIVE_MUX`) düşmesini garantiye almak.
- **Doğrulama:** Ayarlar penceresinden aç/kapat testi ve uçtan uca kayıt doğrulaması.

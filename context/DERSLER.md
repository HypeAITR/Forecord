# Dersler

Her madde: belirti → sebep → çözüm. Aynı hatayı tekrar yapma; yenisini bulursan ekle.

1. **48-byte bozuk MP4 segmenti (`ftyp`-only shell).**
   - **Belirti:** Canlı mux (`FFMPEG_LIVE_MUX`) kaydı durdurulduğunda segment dosyası 48 byte kalıyor ve video oynatılamıyor.
   - **Sebep:** Video stdin kapatılmadan önce ses Named Pipe'larının kapatılması, FFmpeg'in `moov` atomunu yazmadan erken çıkmasına veya takılmasına yol açıyor.
   - **Çözüm:** `RecordingService.EndCurrentSegment()` içindeki kapatma sırasını asla bozma: (1) Capture thread'i durdur, (2) `StopCaptureFlow()` çağır, (3) FFmpeg video stdin'i flush/close et, (4) `DrainLivePipes(2500)` ile ses borularının boşalmasını bekle, (5) `_audio.Stop()` ile boruları kapat, (6) FFmpeg'in çıkmasını bekle (30 sn timeout).

2. **Kayıt sonunda ses kesilmesi veya `0x80070323` oynatıcı hatası.**
   - **Belirti:** Videonun son 1–2 saniyesinde ses kesiliyor ya da Windows Filmler ve TV uygulaması oynatma ortasında hata veriyor.
   - **Sebep:** Varsayılan 4 KB Named Pipe tamponu WASAPI geri çağrılarında taşmaya (overrun) neden oluyor; ayrıca kayıt biterken 1 MB'lık boru tamponu boşalmadan boru kapatılırsa son AAC paketi yarım kalıyor ve saat kayması (clock drift) oluşuyor.
   - **Çözüm:** `AudioService.CreateAudioPipe` içinde 1 MB (`1 << 20`) çıkış tamponu kullan, `DrainLivePipes` ile tamponun okunmasını bekle ve FFmpeg ses filtresinde her zaman `aresample=async=1000:first_pts=0` kullan.

3. **FFmpeg başlangıçta ses borusu beklerken kilitlenme (deadlock).**
   - **Belirti:** Hem mikrofon hem sistem sesi açıkken FFmpeg girdi formatını algılamaya çalışırken (probing) kayıt başlamıyor ve zaman aşımına uğruyor.
   - **Sebep:** FFmpeg birden fazla adlandırılmış boruyu sırayla açıp her birinden ilk ses verisini beklerken WASAPI henüz kayıt başlatmamış oluyor; ya da sistemde o an ses çalmıyorsa `WasapiLoopbackCapture` hiç paket üretmiyor.
   - **Çözüm:** `AudioService.ConnectLivePipesAsync` içinde boru bağlanır bağlanmaz hemen 10 ms'lik sıfır (prime) PCM paketi gönder, `StartSilenceKeeper` ile sessiz çıkış açarak loopback saatini canlı tut ve `StartRecording()`'i bağlantı anında başlat.

4. **İmleç çiziminin kısmi okuma (partial readback) tamponunu kirletmesi.**
   - **Belirti:** `DirtyRect` / `MoveRect` kısmi okuma aktifken fare hareket ettiğinde ekranda eski imleç izleri (hayalet imleç) kalıyor.
   - **Sebep:** `buffer` bir sonraki karede yalnızca değişen bölgelerle güncellendiği için, önceki karede `buffer` üzerine çizilen imleç pikselleri silinmeden kalıyor.
   - **Çözüm:** `TryWriteFrameToPipe` içinde `cap.CompositeCursor(buffer)` çağrıldıktan sonra `finally` bloğunda mutlaka `cap.RestoreCursor(buffer)` çağrılmalıdır.

5. **Bash altında ters eğik çizgi (`\`) ile komut hatası.**
   - **Belirti:** `dotnet build .\ScreenRecorder\ScreenRecorder.csproj` komutu `MSB1009: Proje dosyası yok (.ScreenRecorderScreenRecorder.csproj)` hatası veriyor.
   - **Sebep:** Bash kabuğu tırnaksız `\` karakterlerini kaçış dizisi olarak yutar.
   - **Çözüm:** Bash komutlarında dosya yollarını her zaman `/` ile (`./ScreenRecorder/ScreenRecorder.csproj`) yaz.

6. **AMD AMF `QueryOutput` sırasında `0xc0000409` (`STATUS_STACK_BUFFER_OVERRUN`) çökmesi (`amfrtdrv64.dll`).**
   - **Belirti:** `AmfGpuEncoder.DrainAvailableOutput` içinde ikinci kare kodlandığında uygulama aniden sessizce çöküyor (`amfrtdrv64.dll`, hata kodu `0xc0000409`).
   - **Sebep:** C++'ta `AMFData* pData = nullptr; QueryOutput(&pData);` ile çağrılan fonksiyon, C# P/Invoke tarafında `out IntPtr data` olarak tanımlandığında CLR çağrı öncesinde yığındaki (stack) işaretçiyi sıfırlamaz. `QueryOutput` `AMF_REPEAT` (24) döndürdüğünde yerel sürücü `data` adresine hiç dokunmaz ve önceki döngüden kalan serbest bırakılmış (`Release`'lenmiş) COM işaretçisi `data` içinde kalır; döngünün altındaki `if (data != IntPtr.Zero) ReleaseInterface(data);` satırı ise serbest bırakılmış işaretçiye **double-free** yaparak sürücüyü çökertir.
   - **Çözüm:** `QueryOutput` çağrısından önce yerel değişken mutlaka `var data = IntPtr.Zero;` olarak ilklendirilmeli ve yalnızca `status == AmfOk && data != IntPtr.Zero` olduğunda `ReleaseInterface(data)` çağrılmalıdır.

7. **Windows Media Foundation AAC MFT (`IMFTransform.SetOutputType`) `0xC00D36E6` (`MF_E_ATTRIBUTENOTFOUND`) hatası.**
   - **Belirti:** `MfAacFrameEncoder` içinde sıfırdan `IMFMediaType` yaratılıp `SetOutputType` çağrıldığında `0xC00D36E6` hatası fırlatılıyor.
   - **Sebep:** Microsoft AAC Encoder MFT, giriş (`SetInputType`) ayarlandıktan sonra kendi dahili `AudioSpecificConfig` (`MF_MT_USER_DATA`) özniteliğini içeren aday çıkış tipleri üretir; sıfırdan yaratılan `IMFMediaType` bu öznitelikleri taşımadığı için reddedilir.
   - **Çözüm:** Önce `SetInputType` çağrılmalı, ardından `MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_AAC, ...)` ile MFT'nin kendi sunduğu `IMFMediaType` seçilip `SetOutputType`'a verilmelidir.

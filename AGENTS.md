# Forecord (ScreenRecorder)

Windows 10/11 (x64) için `.NET 8` ve `WPF` ile yazılmış, düşük sistem yükü (zero-overhead hedefli) masaüstü ekran ve ses kayıt uygulamasıdır. DXGI Desktop Duplication tabanlı yakalama, VFR + dirty/move-rect optimizasyonları, WASAPI canlı ses muxing (Named Pipe), ekran üstü çizim (annotation) ve doğrudan GPU kodlama (NVENC / AMF + yerleşik H.264 MP4 muxer) katmanlarını içerir.

Bu dosya giriş noktasıdır. Bir işe başlamadan önce sırayla oku:

1. `context/PROJE.md`: amaç, şu anki durum ve mimari özet
2. `context/KARARLAR.md`: alınmış kararlar (kullanıcı açıkça istemedikçe yeniden tartışma)
3. `context/DERSLER.md`: yaşanmış hatalar ve önlemleri
4. `context/YOL-HARITASI.md`: sıradaki işler
5. `yolharitası.md`: tüm proje dosyalarının ayrıntılı haritası

## Değişmez kurallar

1. **Derleme hedefi sabittir:** Test ve doğrulama derlemelerini yalnızca `ScreenRecorder/bin/DebugCheck` klasörüne yap; kullanıcı açıkça istemedikçe `Release` derlemesi veya `publish` alma ve argümansız `dotnet build` çalıştırma.
2. **Sıralı segment kapatma (`EndCurrentSegment`):** Canlı ses muxing (`FFMPEG_LIVE_MUX`) aktifken önce yakalama iş parçacığını durdur ve video stdin'i kapat, ardından ses borularını boşalt (`DrainLivePipes`), en son ses kaynaklarını kapatıp FFmpeg'in `moov` atomunu yazmasını bekle (48-byte bozuk MP4 hatasını önler).
3. **Kapsam sınırı (DRM):** Mimari performans odaklıdır (`WDA_EXCLUDEFROMCAPTURE` ile yüzen paneli gizler); DRM veya ekran yakalama koruması bypass girişimi kapsam dışıdır.
4. **Bash yol ayracı:** Bash kabuğunda komut çalıştırırken ters eğik çizgi (`\`) kaçış karakteri olarak yutulur; dosya yollarında selalu ileri eğik çizgi (`/`) veya tırnak kullan.

## Hızlı komutlar

```bash
# Doğrulama derlemesi (DebugCheck)
dotnet build ./ScreenRecorder/ScreenRecorder.csproj -c Debug -o ./ScreenRecorder/bin/DebugCheck

# Uygulamayı çalıştırma (PowerShell üzerinden)
powershell -Command "Start-Process './ScreenRecorder/bin/DebugCheck/Forecord.exe'"
```

## Çalışma şekli

- Yeni karar → `context/KARARLAR.md`; yeni hata/ders → `context/DERSLER.md`; durum değişimi → `context/PROJE.md` ve `context/YOL-HARITASI.md`. Bağlam dosyalarını güncel tutmak işin parçasıdır.

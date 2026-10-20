# Doğrulama notları

13 Ekim 2026 — Windows x64, .NET SDK 10.0.401 / runtime 10.0.12.

- Release derlemesi: uygulama, test projesi ve simülatör; 0 hata, 0 uyarı.
- Otomatik testler: 20 başarılı test. Gerçek loopback UDP üzerinden SNMP v1/v2c okuma, standart olmayan tablo indeksleri, negatif sarf değerleri, atık haznesi, eksik tablo, zaman aşımı/tekrar denemesi ve ilerlemeyen OID sınandı.
- HTTP/API: kayıt ekleme/düzenleme/silme, ölçüm/geçmiş, parolaların yanıtlara sızmaması, giriş, çıkış, CSRF ve hedef ağ kısıtı doğrulandı.
- Saklama: şifreli community geri çözüldü; bağlantı hatasında son başarılı ölçüm korundu; ayar değişikliğinden önce başlayan sorgunun yeni kaydı ezmesi engellendi.
- İlk tablo arayüzü tarayıcıda kontrol edildi: demo paneli, kayıt ekleme ve kayıt sonrası test/detay ekranı ile metin araması; masaüstü (1440 px) ve mobil (390 px). `images/dashboard.jpg` bu önceki tasarıma aittir.
- Bağımsız Windows x64 exe çalıştırıldı; sağlık, ana sayfa, JS/CSS ve yazıcı API uçları HTTP 200 döndürdü.
- Kilitli bağımlılık geri yüklemesi (`--locked-mode`) başarılı.
- NuGet restore güvenlik denetiminde ilk SQLite uyarısı güncel paketlere geçilerek giderildi.

Sınırlar: Fiziksel yazıcı, gerçek SNMPv3 cihazı, Windows Service kurulumu, Linux çalışma ortamı, HTTPS/kurumsal proxy ve büyük cihaz filosu bu ortamda doğrulanmadı. GitHub CI yapılandırması hazırlandı fakat uzak depoda henüz çalıştırılmadı.

GitHub’a yayımlama yapılmadı. Hazırlanan kaynak ZIP dosyası çalışma verisi, kimlik bilgisi, makineye bağlı anahtar, bin/obj veya NuGet önbelleği içermez.

## Kart arayüzü güncellemesi

Cihaz tablosu yazıcı simgeli kartlarla değiştirildi; yan menü kaldırıldı, özet sadeleştirildi ve teknik alanlar detay ekranına taşındı. Windows paketi yeniden derlendi. JavaScript sözdizimi, HTML kimlik eşleşmeleri ve kart üretme/arama/filtre işlevleri yerel kod testleriyle kontrol edildi. Bu güncellemenin son tarayıcı kontrolü, tarayıcı güvenlik politikası nedeniyle yapılamadı; yeni görünüm için görsel doğrulama bekliyor. Önceki sürümün tarayıcı kontrolleri yeni tasarımın doğrulandığı anlamına gelmez.

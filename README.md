# Printer Monitor

Fabrika ve ofis ağlarındaki yazıcıların toner/sarf seviyelerini tarayıcıdan takip eden, C# ve ASP.NET Core ile geliştirilmiş yerel uygulama.

**Docker, Node.js ve ayrı bir veritabanı sunucusu gerekmez.** Yazıcı IP’lerini proje geliştiricisine göndermeniz gerekmez: BT ekibi uygulamayı kendi ağına kurar, cihazların IP ve SNMP bilgilerini arayüzden girer.

> v0.1 bir pilot sürümdür. Yazıcısız demo ve gerçek UDP kullanan simülatörle doğrulandı. Fiziksel yazıcıda ve gerçek SNMPv3 cihazında henüz doğrulanmadı. Tüm marka/modellerin toner yüzdesi vereceği garanti edilmez.

Arayüz, yazıcı simgeli kartlardan oluşur. Her kart cihaz adını, konumunu, bağlantı durumunu ve adlarıyla toner seviyelerini gösterir. IP, SNMP bilgileri ve sorgu geçmişi karta tıklayınca açılan detay ekranındadır.

> **Gerçek yazıcı kurulumu:** Kurulum adımları için [Gerçek ağa geçiş](#gerçek-ağa-geçiş) bölümünden başlayın. Demo modu yalnızca yazıcısız önizlemedir; uygulama varsayılan olarak demo modunda açılır ve bu modda gerçek yazıcıya SNMP isteği göndermez.

## Fabrikadaki ekibe nasıl yardımcı olur?

- Yazıcılara tek tek girerek sarf durumu kontrol etme ihtiyacını azaltır.
- Yazıcıları simgeli kartlarda gösterir; cihaz adı, IP ve konumla arama ve durum filtresi sunar.
- Düşük toner ve dolmak üzere olan atık haznesini ayırır.
- Son ölçümü ve son bağlantı denemesini ayrı gösterir.
- Başarısız sorguda önceki ölçümü korur, ekranda eski olduğunu belirtir.
- Son sorguları incelemek ve teknik ekiple paylaşmak için parola içermeyen JSON raporu sunar. Rapor yine IP, konum, cihaz açıklaması ve seri numarası içerir; herkese açık paylaşılmamalıdır.
- Üreticiye özel destek gerektiğinde C# adaptörü eklenebilecek bir başlangıç sunar.

İlk sürümde manuel ekleme/düzenleme/silme, kayıt sonrası bağlantı testi, periyodik sorgulama, anlık yenileme, sarf uyarıları, geçmiş, yönetici girişi ve JSON dışa aktarma vardır. Otomatik alt ağ taraması, e-posta/Teams bildirimleri, çok kullanıcılı yetki yönetimi, baskı işleri, sayfa sayacı ve kağıt sıkışması takibi bu sürümün kapsamına dahil değildir.

## Mimari

```text
Tarayıcı ── HTTP(S) ── ASP.NET Core
                          ├── Web arayüzü ve REST API
                          ├── BackgroundService → SNMP / UDP → Yazıcılar
                          └── SQLite + şifreli erişim bilgileri
```

- **Sunucu:** .NET 10 / ASP.NET Core; web arayüzüyle sorgulama aynı süreçte.
- **Arayüz:** Türkçe HTML/CSS/JavaScript; frontend derlemesi ve harici CDN yok.
- **SNMP:** `Lextm.SharpSnmpLib` 12.5.7; GET ve GETNEXT. SET gönderilmez.
- **Veri:** SQLite, parametreli sorgular; tek uygulama süreci için tasarlanmıştır.
- **Çalıştırma:** Konsol, Windows Service veya Linux systemd. Docker isteğe bağlıdır ve bu projede gerekmemektedir.

Sayfa açılışı cihazları sorgulamaz, kaydedilmiş ölçümleri okur. Arayüz açıkken 15 saniyede bir kayıtları yeniler. Arka plan servisi varsayılan olarak 5 dakikada bir cihazlara sorgu yapar. “Yenile” beklemeden yeni bir tur başlatır.

## SNMP bağlantısı ve toner seviyesi nasıl okunur?

Yazıcı yönetimi için kullanılan protokol **SNMP**’dir; SMTP e-posta gönderme protokolüdür. Yazıcıların SNMP sorgularında kullanılan standart hedef portu **UDP 161**’dir. Uygulama, çalıştığı sunucudan yazıcının IP adresine bu porta okuma isteği gönderir. Arayüzde port alanı değiştirilebilir; farklı port yalnızca yazıcı veya ağ ekibi özel olarak yapılandırdıysa kullanılmalıdır. Simülatör örneğinde `1161` kullanılması, gerçek yazıcıların da bu portu kullandığı anlamına gelmez.

Uygulama standart Printer-MIB tablosundan sarf malzemesinin adını, birimini, kapasitesini ve yazıcının bildirdiği mevcut seviyeyi SNMP GET/GETNEXT istekleriyle okur. Seviye geçerli ve kapasite biliniyorsa ekrandaki yaklaşık yüzde şu şekilde hesaplanır:

```text
kalan yüzde = mevcut seviye / maksimum kapasite × 100
```

Uygulama her baskıyı izlemez ve sayfa başına toner tüketimi hesabı yapmaz. Değeri yazıcı bildirir; bu değerin sensör, kartuş çipi veya üreticinin kullanım tahmininden hangisiyle üretildiği modele ve üreticiye bağlıdır. Dolayısıyla yüzde yaklaşık olabilir, gecikmeli güncellenebilir ya da bazı cihazlarda hiç sunulmayabilir. SNMP bağlantısı başarılı olsa bile toner seviyesi alınamaması mümkündür. Bilinmeyen değerler sıfır toner gibi gösterilmez.

SNMP sorguları salt okunurdur: uygulama toner seviyesini ve standart cihaz bilgilerini GET/GETNEXT ile alır; yazıcı ayarlarını değiştiren SNMP SET isteği göndermez. SNMP okuma yetkisi ve sunucudan yazıcılara UDP çıkışı BT ekibince açılmalıdır. Geniş inbound erişim yerine yalnızca uygulama sunucusundan gerekli yazıcı VLAN’larına erişim tanımlayın.

## Gerçek ağa geçiş

Sunucu yazıcılara erişebilmeli; aynı alt ağda olması zorunlu değildir, yönlendirme ve güvenlik duvarı izinleri yeterlidir. Tarayıcı çalıştıran bilgisayarın yazıcılara doğrudan erişmesi gerekmez.

1. Uygulamayı çalıştıracak Windows/Linux sunucusunu seçin. Bu sunucunun yazıcılara yönlendirilmiş ağ erişimi olmalıdır.
2. BT ekibiyle yazıcılarda SNMP’nin etkin olduğunu, salt okunur erişim bilgisini ve sunucudan yazıcılara **UDP 161** çıkışına izin verildiğini doğrulayın.
3. `src/PrinterMonitor/appsettings.Local.example.json` dosyasını aynı klasörde `appsettings.Local.json` adına kopyalayın.
4. Örnekteki `AllowedNetworks` değerlerini gerçek yazıcı VLAN’larıyla değiştirin. Bu aralıklar sadece uygulamanın sorgu gönderebileceği hedefleri sınırlar; ağ taraması başlatmaz.
5. `DemoMode` değerini `false` yapın ve en az 12 karakterli yönetici parolasını sunucu ortam değişkeninde tanımlayın.
6. Uygulamayı başlatın, giriş yapın ve önce tek bir yazıcı ekleyerek bağlantıyı test edin.

### Hazır Windows paketiyle kurulum

GitHub Releases bölümündeki `PrinterMonitor-win-x64.zip` paketini kullanıyorsanız ZIP’in tamamını sunucuda bir klasöre çıkarın. `Baslat-Demo.cmd` dosyası her zaman demo modunu açar; gerçek kullanımda bu dosya yerine klasörde PowerShell açın. Örnek ağ aralığını kendi yazıcı ağınızla değiştirin:

```powershell
Copy-Item appsettings.Local.example.json appsettings.Local.json
# appsettings.Local.json içindeki AllowedNetworks değerini kurum ağınıza göre düzenleyin.
$env:Monitor__DemoMode = 'false'
$env:Monitor__AllowedNetworks__0 = '192.168.20.0/24'
$credential = Get-Credential -UserName 'admin' -Message 'Yönetici parolası (en az 12 karakter)'
$env:Monitor__AdminPassword = $credential.GetNetworkCredential().Password
.\PrinterMonitor.exe --contentRoot . --urls http://localhost:5080
```

Bu örnek arayüze yalnızca sunucunun kendisinden eriştirir. Fabrika kullanıcıları başka bilgisayarlardan bağlanacaksa dinleme adresi, Windows güvenlik duvarı ve HTTPS/TLS yapılandırmasını BT ekibi yapmalıdır. Yönetici parolasını HTTP üzerinden başka bilgisayarlardan göndermeyin. Bu PowerShell ortam değişkenleri pencere kapatılınca kaybolur; kalıcı Windows Service kurulumu için servis hesabının ortam değişkenlerini veya erişimi kısıtlanmış yerel ayarları ayrıca yapılandırın.

PowerShell’de örnek çalıştırma; örnek IP aralığını kurumunuzun ağıyla değiştirin:

```powershell
$env:Monitor__DemoMode = 'false'
$env:Monitor__AllowedNetworks__0 = '192.168.20.0/24'
$credential = Get-Credential -UserName 'admin' -Message 'Uygulama için en az 12 karakterli yönetici parolası'
$env:Monitor__AdminPassword = $credential.GetNetworkCredential().Password
dotnet run --project src/PrinterMonitor -- --urls http://0.0.0.0:5080
```

Diğer bilgisayarlar `http://SUNUCU-IP:5080` üzerinden ulaşır. Bu komut HTTP ile pilot bağlantı içindir. Yönetici parolasını ağ üzerinden kullanacağınız kalıcı kurulumda ASP.NET Core HTTPS uç noktası veya kurumun TLS sonlandıran ters proxy’si kullanılmalıdır. Ters proxy arkasında forward edilen başlıklar yalnızca bilinen proxy adreslerine güvenecek şekilde yapılandırılmalıdır; bu sürüm otomatik proxy güveni tanımlamaz.

Kalıcı servis, kendi servis hesabına ait ortam değişkenlerini veya `appsettings.Local.json` ayarlarını kullanmalıdır. Yukarıdaki PowerShell değişkenleri sadece o kabuk ve başlattığı alt süreçler için geçerlidir. Yerel ayar dosyasına parola konulursa dosya erişimini servis hesabı ve yöneticilerle sınırlayın; dosya Git tarafından yok sayılır.

### Desteklenen bağlantılar

| Sürüm | Gerekli bilgiler | Durum |
|---|---|---|
| SNMP v1 | Read-only community | Yerel UDP simülatörüyle test edildi |
| SNMP v2c | Read-only community | Yerel UDP simülatörüyle test edildi |
| SNMP v3 | Kullanıcı, SHA-256 veya SHA-1 auth parolası, AES-128 privacy parolası | Uygulandı; gerçek cihaz doğrulaması gerekli |

SNMPv3 bu sürümde **authPriv ve boş context** kullanır. noAuthNoPriv, authNoPriv, özel context, MD5, DES, AES-192/256 arayüzde desteklenmez. SHA-1 yalnızca eski cihaz uyumluluğu seçeneğidir; mümkünse SHA-256 kullanın. Cihazınız desteklemiyorsa uygun adaptör/algoritma geliştirilmelidir.

Yalnızca IPv4 ve IPv4’e çözülen sunucu adları desteklenir. DNS her sorgu başlangıcında yeniden çözülür ve izinli CIDR içinde kalan sayısal adrese istek yapılır.

### Yapılandırma

`Monitor` bölümü ve ortam değişkeni karşılıkları (`Monitor__PollSeconds` gibi):

| Ayar | Varsayılan | Sınır / anlam |
|---|---|---|
| DemoMode | true | Gerçek SNMP için false |
| AdminPassword | tanımsız | Gerçek modda en az 12 karakter |
| AllowedNetworks | boş | Gerçek modda en az bir IPv4 CIDR |
| PollSeconds | 300 | 30–86400 saniye |
| TimeoutMs | 2500 | Her paket için 100–10000 ms |
| Retries | 1 | İlk denemeden sonra 0–2 ek deneme |
| MaxConcurrency | 4 | Aynı anda 1–16 cihaz |
| LowThreshold | 20 | 1–99; tüketilen sarf için kalan ≤ eşik, hazne için doluluk ≥ 100-eşik |
| HistoryDays | 30 | 1–365 gün saklama |
| DataDirectory | data | İçerik köküne göre klasör veya mutlak yol |

Her cihaz okuması toplam 45 saniye ile sınırlıdır. Standart sarf tablosu 512 değişkene, seri numarası tablosu 16 değişkene sınırlandırılır. Sınır aşımı veya ilerlemeyen OID tam ölçüm olarak kaydedilmez. API aynı anda en fazla iki manuel sorgu isteğini kabul eder; aynı yazıcının eşzamanlı ikinci testi devam eden/önceki sonucu döndürebilir. Sayfayı yenileyerek son sonucu görebilirsiniz.

En fazla 500 kayıt sınırı, 500 cihaz için doğrulanmış performans garantisi değildir. Büyük filolarda gerçek ağda tur süresini ölçün; GETNEXT çok sayıda paket oluşturabilir. Bütün turun süresi sorgulama aralığını aşarsa sonraki tur gecikir. İlk sürümü önce az sayıda cihazla pilot olarak değerlendirin.

## Yazıcısız önizleme (Demo modu)

Bu bölüm yalnızca arayüzü fiziksel yazıcı olmadan görmek içindir. Demo modu sahte örnek veriler gösterir, gerçek SNMP/UDP sorgusu göndermez ve sadece aynı bilgisayardan açılabilir. Fabrika ağına kurulum için yukarıdaki gerçek ağ adımlarını izleyin.

Gereksinim: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Komutları depo kökünde çalıştırın:

```powershell
dotnet restore PrinterMonitor.slnx
dotnet run --project src/PrinterMonitor
```

Tarayıcıda **http://localhost:5080** adresini açın. Windows’ta `Start-Demo.ps1` aynı demo modunu başlatır. İlk açılışta normal, düşük tonerli, yanıt vermeyen ve toner seviyesi bildirmeyen dört örnek cihaz oluşturulur. Demo kayıtları `data/demo.db` dosyasında tutulur.

Hazır Windows paketindeki `Baslat-Demo.cmd` daima demo modunu açar. Gerçek yazıcılarla kullanırken bu başlatıcıyı kullanmayın; `appsettings.Local.json` ve ortam değişkenleriyle gerçek modu yapılandırıp `PrinterMonitor.exe` dosyasını doğrudan çalıştırın.

## Fiziksel yazıcı olmadan gerçek SNMP testi

Demo modu uygulama davranışını gösterir. Aşağıdaki simülatör ise **gerçek UDP/SNMP paketlerine** cevap vererek SNMP okuyucusunu test eder. Yalnızca `127.0.0.1` üzerinde dinler, SNMPv1/v2c destekler.

Terminal 1:

```powershell
dotnet run --project tools/PrinterSimulator -- 1161 low
```

Terminal 2 — uygulamayı gerçek okuyucu ile fakat sadece loopback hedeflerine izin vererek başlatın:

```powershell
$env:Monitor__DemoMode = 'false'
$env:Monitor__AllowedNetworks__0 = '127.0.0.1/32'
$credential = Get-Credential -UserName 'admin' -Message 'Yerel test için en az 12 karakterli yönetici parolası'
$env:Monitor__AdminPassword = $credential.GetNetworkCredential().Password
dotnet run --project src/PrinterMonitor
```

Arayüzde yazıcı ekleyin:

| Alan | Değer |
|---|---|
| IP | `127.0.0.1` |
| Port | `1161` |
| SNMP | `v2c` veya `v1` |
| Community | `simulator-read` — yalnızca simülatörün örnek değeri |

Senaryolar: `normal`, `low`, `unknown`, `unsupported`, `timeout`, `loop`. `loop`, hatalı ilerlemeyen OID davranışını sınar. Her farklı sanal cihaz için ayrı port kullanabilirsiniz. Aynı cihazda senaryo değiştirmek için simülatörü durdurup aynı portta yeni senaryoyla başlatın.

## Verinin anlamı ve uyumluluk

Standart alanlar:

| Alan | OID kökü |
|---|---|
| Cihaz açıklaması (sysDescr) | `1.3.6.1.2.1.1.1.0` |
| Seri numarası tablosu | `1.3.6.1.2.1.43.5.1.1.17` |
| Sarf sınıfı | `1.3.6.1.2.1.43.11.1.1.4` |
| Sarf açıklaması | `1.3.6.1.2.1.43.11.1.1.6` |
| Birim | `1.3.6.1.2.1.43.11.1.1.7` |
| Maksimum kapasite | `1.3.6.1.2.1.43.11.1.1.8` |
| Mevcut seviye | `1.3.6.1.2.1.43.11.1.1.9` |

Tablo sonundaki cihaz/sarf indeksleri sabit varsayılmaz; örneğin `.7.10` da okunur. `percent(19)` doğrudan yüzde olarak yorumlanır; diğer birimlerde geçerli seviye ve kapasite ile oran hesaplanır. Negatif özel değerler yüzdeye çevrilmez: `-1/-2` bilinmiyor, `-3` bir miktar var. Hazne doluluğu “kalan toner” olarak yorumlanmaz. Renk etiketleri açıklamadaki renk adlarından çıkarılır; eşleşmeyenler genel sarf olarak gösterilir. Sarf başlıkları ve ham değerler detay ekranında görülebilir.

“Erişilebilir”, SNMP ölçümünün tamamlandığını ifade eder; yazıcının baskıya hazır olduğunu, kağıdının bulunduğunu veya hata vermediğini garanti etmez. SNMP’ye cevap veren ancak sarf tablosu olmayan cihaz “seviye bilgisi yok” şeklinde gösterilir. `sysDescr` gerçek model kodu yerine uzun bir cihaz açıklaması içerebilir.

### Yeni üretici adaptörü

`IPrinterAdapter.ReadAsync(...)` bir `PrinterReading` döndürür. Standart okuyucu `SnmpPrinterAdapter`, demo okuyucu `DemoPrinterAdapter` içindedir. Üreticiye özel bir okuyucu ekleyip `Program.cs` içindeki DI seçimine bağlayabilir veya marka seçen bir üst adaptör oluşturabilirsiniz. Mevcut sürümde hazır HP/Xerox/Ricoh adaptörleri yoktur.

Yeni adaptörde zaman aşımı, iptal, hedef CIDR doğrulaması ve negatif özel değerlerin anlamı korunmalıdır. Üretici OID’sini ve gerçek cihaz çıktısını doğrulamadan modele özel destek iddiasında bulunmayın. Marka isimlerinden sabit indeks/renk sırası çıkarmayın.

## Saklama ve erişim

- Gerçek cihaz kayıtları `data/printers.db`, demo kayıtları `data/demo.db` içindedir.
- SNMP community ve parolaları ASP.NET Core Data Protection ile şifrelenir; API’ye ve JSON raporuna dönmez.
- Windows’ta koruma anahtarları DPAPI ile çalışan kullanıcıya bağlanır. Servis hesabı değişirse eski sırların çözülebilmesi için kontrollü taşıma gerekir; aksi halde erişim bilgilerini yeniden girin.
- Linux’ta anahtar dosyaları varsayılan olarak dosya sistemi izinleriyle korunur. `data/keys` klasörünü yalnızca servis hesabının okuyabildiğinden emin olun. Veritabanı ile anahtar dosyalarını birlikte ele geçiren kişi sırları çözebilir.
- Yönetici girişi tek bir sunucu parolası ve 8 saatlik oturum çerezi kullanır. Rol yönetimi ve kurumsal SSO yoktur. Giriş dakikada 5 denemeyle sınırlıdır; değişiklik istekleri CSRF doğrulaması ister.
- `.gitignore` yerel yapılandırmaları, veri dosyalarını, anahtarları ve derleme çıktılarını dışarıda tutar. Gerçek `snmpwalk` çıktıları ve kurum bilgileri depoya eklenmemelidir.
- Uygulamayı internete açmayın. Kurum içi dağıtımda yönetim arayüzü erişimini ve UDP çıkışını gereken kaynak/hedeflerle sınırlandırın.

Yedek alırken servisi durdurun ve **veri klasörünün tamamını** kopyalayın; çalışan SQLite dosyasını tek başına kopyalamak WAL nedeniyle tutarsız yedek üretebilir. Anahtarları da koruyun. Geri yükleme aynı servis hesabıyla doğrulanmalıdır. Yazıcıyı silme, o kaydı ve geçmişini kalıcı siler; uygulama içi geri alma yoktur, yalnızca yedekten dönebilirsiniz.

## Windows yayımlama ve servis

SDK bulunan geliştirme bilgisayarında:

```powershell
dotnet publish src/PrinterMonitor -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

`publish/win-x64` klasörünün **tamamını** sunucuya kopyalayın; `wwwroot` ve ayar dosyaları gereklidir. Yalnızca exe dosyasını taşımayın. SQLite native kitaplığı ilk çalıştırmada kullanıcının geçici klasörüne çıkarılabilir. Bağımsız paketin .NET güvenlik güncellemeleri, güncel SDK ile yeniden yayımlanarak dağıtılır.

Konsolda:

```powershell
cd publish/win-x64
.\PrinterMonitor.exe --contentRoot . --urls http://localhost:5080
```

Windows Service kurulumu, yönetici PowerShell’inde kurumun belirlediği servis hesabıyla yapılabilir. Örnek yolu kendi dağıtım klasörünüzle değiştirin:

```powershell
$serviceCredential = Get-Credential -Message 'Log on as a service yetkili servis hesabı'
New-Service -Name PrinterMonitor -DisplayName 'Printer Monitor' -StartupType Automatic `
  -Credential $serviceCredential `
  -BinaryPathName '"C:\Apps\PrinterMonitor\PrinterMonitor.exe" --contentRoot "C:\Apps\PrinterMonitor"'
Start-Service PrinterMonitor
Get-Service PrinterMonitor
```

Önce servis hesabının uygulama klasörünü okuyabildiğini, `data` ve geçici klasöre yazabildiğini, gerekli ağ erişimine sahip olduğunu doğrulayın. `appsettings.Local.json` içine gerçek ağ, dinleme adresi ve servis ortamına uygun ayarları koyun. Konsoldaki ortam değişkenleri Windows Service’e otomatik aktarılmaz. Bu proje kurulumu kendiliğinden servis oluşturmaz veya güvenlik duvarını değiştirmez.

Linux için `docs/printer-monitor.service.example` örneğini uyarlayın. Ayrı bir servis kullanıcısı oluşturun, yayımlanmış klasörün okunmasını ve veri dizininin yazılmasını sağlayın. Varsayılan örnek localhost üzerinde dinler; ağ sunumu için HTTPS/proxy düzenini kurun.

## Testler

```powershell
dotnet test PrinterMonitor.slnx
```

Testler yalnızca loopback UDP hedeflerine ve geçici test veritabanlarına erişir. Gerçek fabrika ağını taramaz. Yüzde hesaplama, özel değerler, CIDR kısıtı, standart olmayan indeksler, atık haznesi, zaman aşımı/tekrar denemesi, ilerlemeyen OID, önceki ölçümün korunması, değiştirilen kaydın eski sorguyla ezilmemesi, API CRUD, giriş, CSRF ve sırsız rapor sınanır.

Tarayıcıda ayrıca manuel kontrol: yazıcı ekle → kaydet/test et → düşük toner kartına tıklayarak detayını aç → arama/filtre → düzenle → yeni senaryoyu test et. Kartlar dar ekranlarda tek sütuna geçecek şekilde düzenlenmiştir.

Fabrikada pilot kabul sırası: önce tek yazıcıda gerçek SNMP değerlerini cihazın yönetim ekranıyla karşılaştırın, sonra her marka/modelden bir örnek deneyin. SNMPv3 için doğru/yanlış kimlik, yeniden başlatma ve engine time davranışını ayrıca doğrulayın. Sonra cihaz sayısını artırıp sorgu tur süresini gözlemleyin.

## Sorun giderme

| Belirti | Kontrol |
|---|---|
| SNMP yanıtı yok | IP/UDP portu, SNMP açık mı, read-only bilgileri, VLAN/ACL; neden tek yanıtsız istekle kesinleşmez |
| Ping çalışıyor, SNMP çalışmıyor | ICMP ve UDP farklıdır; UDP 161 izinlerini kontrol edin |
| SNMP var, toner yok | Standart Printer-MIB desteği veya üreticiye özel MIB gerekebilir |
| Hedef engellendi | `AllowedNetworks` CIDR listesi ve DNS’nin çözüldüğü adres |
| Eski ölçüm görünüyor | Son deneme hatasını, servis erişimini ve zamanını kontrol edin |
| Gerçek mod başlamıyor | En az 12 karakterli yönetici parolası ve izinli ağ listesi gerekli |
| Sırlar çözülemiyor | Veri anahtarları silinmiş/servis hesabı değişmiş olabilir; yedek veya yeni erişim bilgileri |
| Başka PC demo ekranına giremiyor | Demo loopback ile sınırlıdır; gerçek mod ve dinleme adresini yapılandırın |
| Giriş/işlem reddediliyor | CSRF için sayfayı yenileyin; 429 için süren sorguyu veya giriş limitini bekleyin |

## Proje dizini

```text
src/PrinterMonitor/       Uygulama, servisler, SQLite ve web arayüzü
tests/PrinterMonitor.Tests/  Otomatik API ve UDP testleri
tools/PrinterSimulator/   Loopback SNMP v1/v2c simülatörü
docs/                    Dağıtım örnekleri ve doğrulama notları
.github/workflows/       Windows/Linux CI
```

GitHub’a koymadan önce yerel sır ve ölçüm kayıtlarının dışarıda kaldığını `git status` ile kontrol edin. `packages.lock.json` dosyalarını sürüm kontrolünde tutun. Lisans tercihi henüz belirlenmedi; kamuya açık kullanım koşulları için uygun bir LICENSE dosyası eklenmelidir.

Referanslar: [Printer-MIB RFC 3805](https://www.rfc-editor.org/rfc/rfc3805), [SharpSnmpLib kaynak kodu](https://github.com/lextudio/sharpsnmplib), [ASP.NET Core Windows Service](https://learn.microsoft.com/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0).

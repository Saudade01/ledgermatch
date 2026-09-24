# Mutabakat Servisi

[![CI](https://github.com/Saudade01/mutabakat-servisi/actions/workflows/ci.yml/badge.svg)](https://github.com/Saudade01/mutabakat-servisi/actions/workflows/ci.yml)

İki sistemin ödeme kayıtlarını karşılaştıran küçük bir .NET API. Örneğin şirket kaydındaki `INV-101` için tutar `12500` kuruş, ödeme sağlayıcısında `12400` kuruşsa iki kayıt korunur ve `amount_mismatch` sonucu üretilir.

Sentetik verilerle geliştirilen yerel bir portföy prototipidir. Gerçek müşteride veya finans sisteminde doğrulanmadı. Banka/ERP bağlantısı ve yapay zekâ bileşeni yoktur. [English documentation](README.md).

## Çalıştırma

Docker Compose ve Python 3 kurulu olmalıdır. Depo kökünde:

```sh
docker compose up --build -d
python3 scripts/demo.py --base-url http://localhost:5087
```

Demoyu çalıştırmadan önce `http://localhost:5087/health` adresinin HTTP 200 dönmesini bekleyin. API portu `5087`, PostgreSQL portu `55439`. Compose içindeki parola yalnızca yerel demo içindir.

Demo, [şirket kayıtlarını](samples/ledger.csv) JSON, [sağlayıcı kayıtlarını](samples/provider.csv) CSV olarak yükler. Sonuçları [beklenen çıktıyla](samples/expected.json) karşılaştırır; tekrar gönderim, aynı anahtarla farklı içerik, hatalı tutar, kaydedilmiş sonuç ve CSV dışa aktarımını kontrol eder. Bir kontrol başarısızsa sıfırdan farklı çıkış kodu döner. Aynı komut yeniden çalıştırılabilir; aynı kayıt ve mutabakat kimlikleri kullanılır. Ayrı veri seti için `--key-prefix baska-demo` eklenebilir.

## Örnek senaryolar

| Referans | Beklenen davranış |
| --- | --- |
| INV-100 | Eşit TRY tutarları eşleşir |
| INV-101 | 12500 ve 12400 kuruş arasındaki fark raporlanır |
| INV-102 | Sağ tarafta eksik |
| INV-103 | Sol tarafta eksik |
| INV-104 | Solda iki kayıt var; toplamları sağdaki tutara eşit olsa da yinelenen referans olarak ayrılır |
| INV-105 | Eşit EUR tutarları eşleşir |
| INV-106 | TRY ve EUR kayıtları iki ayrı eksik grup oluşturur |

Örnekte 7 şirket kaydı, 6 sağlayıcı kaydı ve 8 sonuç grubu bulunur. Toplamlar para birimi bazında tutulur; fark sol eksi sağdır. Bunlar beklenen sonucu bilinen örneklerdir; üretim ortamı başarı ölçümü değildir.

## Kurallar

CSV başlığı: `sourceRecordId,reference,currency,amountMinor`. Tutar kuruş/cent cinsinden negatif olmayan tam sayıdır; `12500`, 125,00 TRY veya EUR anlamına gelir. Yalnızca TRY ve EUR desteklenir.

Referansın başındaki ve sonundaki boşluk temizlenir; büyük/küçük harf korunur. Benzer isimden tahmini eşleştirme yapılmaz. Aynı referans ve para biriminde bir tarafta birden çok kayıt varsa sonuç `duplicate` olur. Hatalı içe aktarmada dosyanın tamamı reddedilir.

Veri setleri değiştirilemez. `Idempotency-Key` aynı içerikle tekrar kullanılırsa mevcut veri seti döner; farklı içerikle kullanılırsa HTTP 409 alınır. JSON ve CSV aynı anahtar alanını paylaşır. Aynı sıralı veri seti çifti tekrar karşılaştırıldığında kaydedilmiş sonuç döner. Endpoint listesi [İngilizce README](README.md#api) ve `/openapi/v1.json` içindedir.

## Testler

.NET 10 SDK ile, veritabanına ihtiyaç duymayan çekirdek testleri:

```sh
dotnet test tests/Mutabakat.Core.Tests
```

API testleri çalışan PostgreSQL ve ayrı test veritabanı/şeması oluşturma yetkisi ister:

```sh
RECON_TEST_DB='Host=localhost;Port=55439;Database=mutabakat;Username=mutabakat;Password=local-demo-only' dotnet test tests/Mutabakat.Api.Tests
```

## Sınırlar

Kimlik doğrulama/yetkilendirme yoktur; yerelde çalıştırılmalıdır. İade, komisyon, kur dönüşümü, parçalı ödeme, bölünmüş transfer, valör tarihi ve elle çözümleme kapsam dışındadır. Eşleşme, yalnızca verilen referans, para birimi ve tutarın aynı olduğunu gösterir; ödemenin gerçekleştiğini kanıtlamaz. İstek başına sınır 10.000 kayıt ve 2 MiB'tır. [Tasarım kararları](docs/decisions.md).

# MD Reader

خواننده‌ی سبک فایل‌های Markdown با C# و [Avalonia](https://avaloniaui.net/). برنامه روی **ویندوز، لینوکس، مک و اندروید** اجرا می‌شود و برای نمایش Markdown به مرورگر، WebView یا اینترنت نیازی ندارد.

## امکانات

- باز کردن فایل‌های `.md` (و `.txt`) با دکمه، میان‌بر یا کشیدن فایل روی پنجره (کشیدن فایل مخصوص دسکتاپ است)
- هر فایل در **تب جداگانه** باز می‌شود: انتخاب چند فایل با هم، جابه‌جایی بین تب‌ها، بستن با ✕ یا `Ctrl + W`، و نگه‌داشتن جای اسکرول هر تب
- کلید **راست به چپ (RTL)** برای نمایش کامل سند از راست به چپ (فهرست‌ها، نقل‌قول‌ها و جدول‌ها هم قرینه می‌شوند و کد همیشه چپ‌به‌راست می‌ماند)
- انتخاب فونت از میان همه‌ی فونت‌های نصب‌شده‌ی سیستم؛ تغییر فونت روی کل فایل اعمال می‌شود
- تغییر اندازه‌ی فونت (همه‌ی بخش‌ها، از جمله تیترها و کد، متناسب بزرگ/کوچک می‌شوند)
- نمایش تیترها، متن ضخیم و مورب، خط‌خورده، کد درون‌خطی و بلوکی، فهرست ساده/شماره‌دار/وظایف، نقل‌قول، جدول، خط جداکننده و پیوندهای قابل کلیک
- انتخاب و کپی متن سند
- نوار وضعیت با تعداد کلمه و کاراکتر
- خواندن فایل‌های UTF-8 (با یا بدون BOM)، UTF-16 و فایل‌های قدیمی Windows-1256

## پیش‌نیاز

- **.NET SDK 8** برای ساخت نسخه‌ی دسکتاپ، یا **.NET 8 Runtime** برای اجرای نسخه‌ی وابسته به رانیتایم
- ویندوز ۱۰/۱۱، لینوکس (X11 یا Wayland) یا macOS 11 یا بالاتر
- برای ساخت اندروید: **.NET SDK 10**، ورک‌لود `android` و **JDK 17** (بخش [ساخت نسخه‌ی اندروید](#ساخت-نسخهی-اندروید))

## اجرا

```bash
dotnet run -- sample.md          # باز کردن یک فایل
dotnet run -- sample.md --rtl    # شروع با حالت راست‌به‌چپ روشن
dotnet run                       # بدون فایل (صفحه‌ی خالی)
```

ساخت و اجرای مستقیم:

```bash
dotnet build -c Release
./bin/Release/net8.0/MdReader sample.md      # لینوکس/مک
bin\Release\net8.0\MdReader.exe sample.md    # ویندوز
```

## انتشار برای هر سیستم‌عامل

فایل‌های اجرایی مستقل (بدون نیاز به نصب .NET روی سیستم مقصد):

```bash
dotnet publish -c Release -r win-x64   --self-contained true -o dist/win-x64
dotnet publish -c Release -r linux-x64 --self-contained true -o dist/linux-x64
dotnet publish -c Release -r osx-arm64 --self-contained true -o dist/osx-arm64
dotnet publish -c Release -r osx-x64   --self-contained true -o dist/osx-x64
```

اگر روی سیستم مقصد .NET 8 نصب است، `--self-contained false` حجم خروجی را کم می‌کند.

## ساخت نسخه‌ی اندروید

هد اندروید (`MdReader.Android/`) از Avalonia 12 استفاده می‌کند که فقط برای `net10.0-android` ساخته شده؛ پس برخلاف بقیه‌ی برنامه‌ها به **.NET SDK 10** نیاز دارد. یک‌بار ابزارها را نصب کنید:

```bash
dotnet workload install android   # چند گیگابایت فضا و احتمالاً دسترسی ادمین/root می‌خواهد
java -version                     # باید JDK 17 باشد
```

سپس:

```bash
dotnet publish MdReader.Android/MdReader.Android.csproj -c Release -f net10.0-android \
    -p:AndroidPackageFormats=apk -o out/android
```

خروجی: `out/android/com.smrmoosavi.mdreader-Signed.apk`. برای نصب روی گوشی، «نصب از منابع نامشخص» را برای فایل‌منیجر یا مرورگر اجازه بدهید و APK را باز کنید. اجرا روی گوشی/امولاتور وصل‌شده هم ممکن است:

```bash
dotnet build MdReader.Android/MdReader.Android.csproj -t:Run -f net10.0-android
```

تنظیمات فعلی: package id `com.smrmoosavi.mdreader`، حداقل اندروید ۷ (API 24)، خروجی APK و امضا با کلید debug (برای نصب دستی کافی است). برای انتشار در Google Play باید keystore بسازید، `AndroidKeyStore` و کلید امضا را در `MdReader.Android.csproj` تنظیم کنید و خروجی را به AAB تغییر بدهید.

باز کردن فایل در اندروید با انتخاب‌گر فایل سیستم انجام می‌شود و محتوا از استریم خوانده می‌شود (نه از مسیر فایل)، بنابراین با scoped storage اندروید سازگار است؛ دکمه‌ی «بازخوانی» هم همان فایل انتخاب‌شده را دوباره می‌خواند. کشیدن فایل روی پنجره فقط در دسکتاپ معنی دارد.

## میان‌برها

| کلید | کار |
| --- | --- |
| `Ctrl + O` | باز کردن فایل در تب جدید |
| `Ctrl + W` | بستن تب فعلی |
| `F5` | بازخوانی فایل فعلی |
| `Ctrl + +` / `Ctrl + −` | بزرگ و کوچک کردن فونت |
| `Ctrl + چرخ ماوس` | تغییر اندازه‌ی فونت |
| `Ctrl + 0` | بازگشت به اندازه‌ی پیش‌فرض |

## نصب روی ویندوز (برنامه‌ی نصب)

پوشه‌ی `Installer/` یک برنامه‌ی نصب کوچک (WPF، مخصوص ویندوز) است که نسخه‌ی منتشرشده‌ی برنامه را در خود جاسازی می‌کند:

```powershell
powershell -ExecutionPolicy Bypass -File Installer\build-installer.ps1
```

خروجی: `Installer\dist\MdReaderSetup.exe`. با اجرای آن:

- برنامه در `%LOCALAPPDATA%\Programs\MdReader` نصب می‌شود (بدون نیاز به دسترسی ادمین)
- برنامه به‌عنوان بازکننده‌ی فایل‌های `.md`، `.markdown` و `.mdown` ثبت می‌شود و آیکون فایل‌ها به آیکون MD Reader تغییر می‌کند
- میان‌بر منوی استارت (و به‌صورت اختیاری دسکتاپ) ساخته می‌شود
- برنامه در **Settings → Apps → Installed apps** فهرست می‌شود و از همان‌جا (یا با `Uninstall.exe`) حذف می‌شود

سوییچ‌های خط فرمان نصب‌کننده (برای نصب/حذف بی‌صدا):

```text
MdReaderSetup.exe [--install|--uninstall] [--silent] [--dir <path>] [--ext .md]
                  [--no-associate] [--no-shortcuts] [--desktop] [--from-temp]
```

## ثبت به‌عنوان بازکننده‌ی فایل‌های Markdown در لینوکس و مک

در **macOS** پس از اجرای برنامه، روی یک فایل `.md` راست‌کلیک کنید → **Open With** → **Other…** → MD Reader → تیک **Always Open With**.

در **لینوکس** یک فایل `~/.local/share/applications/mdreader.desktop` بسازید:

```ini
[Desktop Entry]
Type=Application
Name=MD Reader
Exec=/path/to/MdReader %f
Icon=/path/to/assets/MdReader.png
MimeType=text/markdown;
Terminal=false
```

سپس:

```bash
xdg-mime default mdreader.desktop text/markdown
```

## ساختار پروژه

- `MdReader.csproj` / `Program.cs` — هد دسکتاپ (بخش فقط‌ویندوز/دسکتاپ برنامه)
- `MdReader.Shared/` — کتابخانه‌ی مشترک: `App.axaml`، `MainView.axaml(.cs)` (کل رابط کاربری)، `Rendering/MarkdownRenderer.cs` و `assets/`
- `MdReader.Android/` — هد اندروید: `MainActivity`، مانیفست، آیکون و تم
- `Installer/` — برنامه‌ی نصب ویندوز (پروژه‌ی جدا؛ در ساخت برنامه‌ی اصلی نادیده گرفته می‌شود)

رابط کاربری در `MainView` است، بنابراین دسکتاپ (پنجره) و اندروید (تک‌ویو) همان کد را اجرا می‌کنند. پنجره‌ی دسکتاپ و آیکونش در `MdReader.Shared/App.axaml.cs` ساخته می‌شوند و `MainWindow` جداگانه‌ای وجود ندارد.

## ارسال به GitHub

```bash
git remote add origin https://github.com/<user>/<repo>.git
git branch -M main
git push -u origin main
```

## انتشار خودکار با GitHub Actions

Workflow در `.github/workflows/release.yml` با هر تگ اجرا می‌شود و برای هر سه سیستم‌عامل خروجی مستقل می‌سازد:

```bash
git tag v1.0.0
git push origin v1.0.0
```

نتیجه در صفحه‌ی **Releases** همان تگ قابل دانلود است:

- `MdReaderSetup.exe` — برنامه‌ی نصب ویندوز (از نسخه‌ی win-x64 ساخته می‌شود)
- `MdReader-win-x64.zip`
- `MdReader-linux-x64.tar.gz`
- `MdReader-osx-arm64.tar.gz` (مک با پردازنده‌ی Apple Silicon)
- `MdReader-osx-x64.tar.gz` (مک با پردازنده‌ی Intel)
- `com.smrmoosavi.mdreader-Signed.apk` (اندروید؛ با کلید debug امضا شده و برای نصب دستی کافی است)

این نسخه‌ها self-contained هستند و به نصب .NET روی سیستم مقصد نیازی ندارند. اجرای دستی هم از **Actions → Release → Run workflow** ممکن است (بدون ساخت Release، فقط artifact).

APK اندروید در یک job جداگانه روی `ubuntu-latest` ساخته می‌شود: نصب ورک‌لود `android` + JDK 17 روی SDK 10، سپس `dotnet publish` با `AndroidPackageFormats=apk`.

`MdReaderSetup.exe` در یک job جداگانه روی `windows-latest` ساخته می‌شود: ابتدا نسخه‌ی win-x64 منتشر و به‌عنوان `payload.zip` در نصب‌کننده جاسازی می‌شود، سپس `Installer/Installer.csproj` (WPF، هدف `net10.0-windows`) به یک فایل اجرایی تک‌فایلی تبدیل می‌شود. این job از **.NET SDK 10** استفاده می‌کند، در حالی که خود برنامه با SDK 8 ساخته می‌شود.

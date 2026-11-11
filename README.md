# MD Reader

خواننده‌ی سبک فایل‌های Markdown با C# و [Avalonia](https://avaloniaui.net/). برنامه روی **ویندوز، لینوکس و مک** اجرا می‌شود و برای نمایش Markdown به مرورگر، WebView یا اینترنت نیازی ندارد.

## امکانات

- باز کردن فایل‌های `.md` (و `.txt`) با دکمه، میان‌بر یا کشیدن فایل روی پنجره
- کلید **راست به چپ (RTL)** برای نمایش کامل سند از راست به چپ (فهرست‌ها، نقل‌قول‌ها و جدول‌ها هم قرینه می‌شوند و کد همیشه چپ‌به‌راست می‌ماند)
- انتخاب فونت از میان همه‌ی فونت‌های نصب‌شده‌ی سیستم؛ تغییر فونت روی کل فایل اعمال می‌شود
- تغییر اندازه‌ی فونت (همه‌ی بخش‌ها، از جمله تیترها و کد، متناسب بزرگ/کوچک می‌شوند)
- نمایش تیترها، متن ضخیم و مورب، خط‌خورده، کد درون‌خطی و بلوکی، فهرست ساده/شماره‌دار/وظایف، نقل‌قول، جدول، خط جداکننده و پیوندهای قابل کلیک
- انتخاب و کپی متن سند
- نوار وضعیت با تعداد کلمه و کاراکتر
- خواندن فایل‌های UTF-8 (با یا بدون BOM)، UTF-16 و فایل‌های قدیمی Windows-1256

## پیش‌نیاز

- **.NET SDK 8** برای ساخت، یا **.NET 8 Runtime** برای اجرای نسخه‌ی وابسته به رانیتایم
- ویندوز ۱۰/۱۱، لینوکس (X11 یا Wayland) یا macOS 11 یا بالاتر

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

## میان‌برها

| کلید | کار |
| --- | --- |
| `Ctrl + O` | باز کردن فایل |
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

- `Program.cs` / `App.axaml` — نقطه‌ی شروع و استایل‌های رابط کاربری
- `MainWindow.axaml` / `MainWindow.axaml.cs` — نوار ابزار، باز کردن فایل، تغییر فونت، RTL و نوار وضعیت
- `Rendering/MarkdownRenderer.cs` — تبدیل خروجی Markdig به کنترل‌های Avalonia
- `assets/MdReader.ico` و `assets/MdReader.png` — آیکون برنامه (چندسایز برای ویندوز، PNG برای پنجره و لینوکس)
- `Installer/` — برنامه‌ی نصب ویندوز (پروژه‌ی جدا؛ در ساخت برنامه‌ی اصلی نادیده گرفته می‌شود)

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

- `MdReader-win-x64.zip`
- `MdReader-linux-x64.tar.gz`
- `MdReader-osx-arm64.tar.gz` (مک با پردازنده‌ی Apple Silicon)
- `MdReader-osx-x64.tar.gz` (مک با پردازنده‌ی Intel)

این نسخه‌ها self-contained هستند و به نصب .NET روی سیستم مقصد نیازی ندارند. اجرای دستی هم از **Actions → Release → Run workflow** ممکن است (بدون ساخت Release، فقط artifact).

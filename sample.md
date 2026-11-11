# راهنمای خواندن فایل‌های Markdown

این یک نمونه فایل برای آزمایش **برنامه MD Reader** است. متن فارسی باید درست و پیوسته نمایش داده شود و با روشن کردن حالت راست‌به‌چپ، همه چیز از سمت راست شروع شود.

## امکانات

- پشتیبانی از متن **ضخیم**، *مورب* و ~~خط‌خورده~~
- کد درون‌خطی مثل `dotnet build`
- پیوندها: [مستندات Markdig](https://github.com/xoofx/markdig)
- فهرست وظایف:
  - [x] باز کردن فایل
  - [ ] تغییر فونت

### فهرست شماره‌دار

1. فایل را باز کنید
2. فونت دلخواه را انتخاب کنید
3. حالت RTL را روشن یا خاموش کنید

> نقل‌قول: نمایش نقل‌قول‌ها با خط کنار متن، در هر دو حالت چپ‌به‌راست و راست‌به‌چپ.

## نمونه کد

```csharp
var reader = new StreamReader(path, Encoding.UTF8);
var text = reader.ReadToEnd();
```

## جدول

| ویژگی | وضعیت | توضیح |
| --- | --- | --- |
| RTL | ✅ | قابل فعال و غیرفعال کردن |
| فونت | ✅ | تغییر برای کل فایل |
| اندازه فونت | ✅ | کوچک و بزرگ کردن |

---

Paragraph in English: Markdown Reader renders **bold**, *italic*, `inline code` and links in a mixed document, and each script keeps its own direction.

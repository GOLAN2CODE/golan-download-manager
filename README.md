# جولان — مدير التنزيل (Golan Download Manager — GDM)

[![Download](https://img.shields.io/github/v/release/dark9guna/golan-download-manager-releases?label=%D8%AA%D9%86%D8%B2%D9%8A%D9%84%20%2F%20Download&color=FDD103&labelColor=111111)](https://github.com/dark9guna/golan-download-manager-releases/releases/latest)
[![CI](https://github.com/dark9guna/golan-download-manager/actions/workflows/ci.yml/badge.svg)](https://github.com/dark9guna/golan-download-manager/actions/workflows/ci.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows)

**الإصدار 2.5.0** · تطبيق سطح مكتب لنظام ويندوز 10/11 مبني على **.NET 10 (WPF)** مع إضافة متصفح متكاملة (Manifest V3) لمتصفحات كروميوم وفايرفوكس.

---

## لمحة عامة

**جولان (GDM)** هو مدير ومسرّع تنزيلات متكامل لنظام ويندوز، مصمّم بواجهة عربية أصيلة (من اليمين إلى اليسار) وإنجليزية كاملة (من اليسار إلى اليمين)، مع وضع داكن ووضع فاتح بهوية جولان البصرية (`#FDD103` / `#131313`)، وتكامل مباشر مع المتصفحات، ومحرك تنزيل متعدّد الاتصالات يدعم التقسيم الديناميكي، استخراج الفيديو والصوت، بروتوكول التورنت والروابط المغناطيسية، والتحكم عن بُعد عبر الشبكة المحلية (HTTPS + رمز QR).

---

## المزايا الرئيسية

### 1. محرك التنزيل الأساسي (HTTP / HTTPS / سحابي)
- **تقسيم ديناميكي للاتصالات (Dynamic Segment Splitting):** تقسيم الملف إلى ما يصل إلى 32 اتصالاً متوازياً، مع تقسيم الجزء الأكبر تلقائياً عند فراغ أي اتصال دون إيقاف البث الجاري، وحفظ الأجزاء غير المكتملة بامتداد `.gdm`.
- **الإيقاف المؤقت والاستئناف الموثوق:** حفظ فوري لحالة الأجزاء (`ETag` / `Last-Modified` / الموضع) واستئناف التنزيل من النقطة التي توقف عندها حتى بعد إعادة تشغيل النظام.
- **تحديث عنوان التنزيل (مثل IDM):** إذا انتهت صلاحية الرابط يقترح جولان التحديث بإشعار، أو من «التنزيلات ← تحديث رابط التنزيل…» أو القائمة اليمنى. يفتح صفحة الموقع، وعند الضغط على زر التنزيل مرة أخرى يتعرّف على الملف نفسه (بالاسم أو بالحجم) ويكمل من حيث توقف. إذا جاء الرابط الجديد من خادم آخر يقارن جولان عيّنات من البايتات المنزّلة قبل المتابعة، وأي اختلاف يعني ملفًا آخر فيبدأ من جديد بدل خلطه بالقديم.
- **محدد سرعة دقيق (Token-Bucket Rate Limiter):** تحديد السرعة الإجمالية أو سرعة كل ملف على حدة، مع **قواعد سرعة زمنية (Speed Rules)** لتغيير الحد تلقائياً حسب أيام الأسبوع وساعات اليوم.
- **قوائم انتظار ومجدول ذكي:** قائمة انتظار رئيسية وقائمة مزامنة دورية وقوائم مخصّصة، مع جدولة البدء والإيقاف والتكرار وإجراءات ما بعد الانتهاء (إغلاق البرنامج، السكون، السبات، إيقاف تشغيل الحاسوب مع نافذة عدّ تنازلي للإلغاء).
- **محلّلات الروابط السحابية (Host Resolvers):** استخراج روابط التنزيل المباشرة من **GitHub Releases**، و**Google Drive**، و**Dropbox**، و**OneDrive**، و**MediaFire**، و**MEGA** (مع فك تشفير AES-CTR/CBC).
- **معاينة الأرشيف قبل التنزيل (ZIP Preview):** قراءة الفهرس المركزي لملفات ZIP البعيدة عبر طلبات `Range` لعرض محتويات الأرشيف دون تنزيله بالكامل.
- **ملتقط المواقع (Site Grabber) وتنزيل الدفعات (Batch):** زحف صفحات الويب بعمق قابل للتخصيص وفلاتر امتدادات، وتوليد سلاسل الروابط بأنماط رقمية أو حرفية (`*`).

### 2. تنزيل الفيديو والصوتيات (Media Downloader)
- دعم أكثر من **1800 موقع** (بما فيها يوتيوب والمنصات التعليمية والإخبارية) عبر تكامل معزول وآمن مع `yt-dlp` و`ffmpeg`.
- اختيار الجودة والصيغة (`4K 2160p`، `1440p`، `1080p`، `720p`، `480p`، `360p`، أو استخلاص الصوت `MP3` / `M4A`).
- دعم تنزيل **قوائم التشغيل (Playlists)**، ودمج **الترجمات (Subtitles)**، وتمرير ملفات تعريف الارتباط بشكل مؤقت ومشفّر.

### 3. دعم التورنت والروابط المغناطيسية (BitTorrent & Magnet)
- محرك تورنت مدمج عبر **MonoTorrent** يدعم ملفات `.torrent` وروابط `magnet:?xt=urn:btih:...`.
- اختيار الملفات الفرعية وأولوياتها قبل البدء أو أثناء التنزيل.
- التحكم في نسبة الرفع (Seed Ratio)، ودعم DHT وPEX وLSD وUPnP/NAT-PMP، مع إمكانية تسجيل البرنامج معالجاً افتراضياً لروابط `magnet:` في سجل المستخدم (`HKCU`).

### 4. تكامل المتصفحات (Chrome / Edge / Brave / Opera / Vivaldi / Firefox)
- إضافة متصفح مبنية وفق معيار **Manifest V3** تتواصل مع تطبيق سطح المكتب عبر **Native Messaging** (`com.golan.gdm`).
- اعتراض تلقائي للتنزيلات مع احترام قائمة المواقع المستثناة ومفاتيح التجاوز (`Alt + نقر` للتنزيل بالمتصفح، و`Alt + Ctrl + نقر` لفرض التنزيل عبر جولان).
- زر عائم معزول داخل `Shadow DOM` فوق مشغلات الفيديو في صفحات الويب، وقوائم سياق للنقر الأيمن.

### 5. الأمان، التحقق من السلامة، وفك الضغط التلقائي
- **حماية البيانات (DPAPI):** تشفير كلمات المرور وملفات تعريف الارتباط محلياً بنطاق المستخدم الحالي (`CurrentUser`)، مع منع تسريب بيانات الاعتماد عند إعادة التوجيه إلى نطاق مختلف أو عند الانحدار من HTTPS إلى HTTP.
- **التحقق من البصمة الرقمية (Integrity Verification):** حساب ومطابقة `MD5` و`SHA-1` و`SHA-256` و`SHA-512`، مع اكتشاف تلقائي لملفات `.sha256` / `.md5` المرافقة.
- **فك الضغط التلقائي الآمن (Safe Auto-Extractor):** استخراج أرشيفات `.zip` مدمجاً، وأرشيفات `.7z` / `.rar` / `.tar.*` عبر 7-Zip مع فحص مسبق يمنع ثغرات اجتياز المسار (Zip-Slip) والروابط الرمزية (Smlinks) وقنابل الضغط (Zip Bombs).
- **وسم مصادر الإنترنت (Mark of the Web):** كتابة دفق `Zone.Identifier` (`ZoneId=3`) مع تعقيم كامل من محارف السطر الجديد لمنع حقن إعدادات INI.

### 6. التحكم عن بُعد عبر الشبكة المحلية (HTTPS Remote Control)
- خادم ويب محلي مدمج (Kestrel) يعمل عبر **HTTPS (TLS بشهادة ECDSA P-256 ذاتية التوقيع محمية عبر DPAPI)**، ويقتصر على عناوين الشبكة المحلية (`127.0.0.1` / `192.168.x.x` / `10.x.x.x` / `172.16-31.x.x`).
- إقران الأجهزة المحمولة عبر **رمز PIN عشوائي من 6 أرقام** صالح لمدة 5 دقائق لمرة واحدة، أو عبر مسح **رمز QR** المدمج (يتضمن بصمة شهادة SHA-256 للتحقق الموثوق)، مع حظر مؤقت لعنوان IP بعد 5 محاولات خاطئة.

### 7. التثبيت المرن والوضع المحمول
- **مثبّت مدمج بدون صلاحيات مسؤول:** الخيار `GDM.exe --install` (أو من قائمة «التعليمات ← تثبيت جولان على هذا الجهاز…») ينسخ البرنامج إلى `%LOCALAPPDATA%\Programs\Golan\GDM\GDM.exe` وينشئ اختصارات سطح المكتب وقائمة «ابدأ» ويسجّل `HKCU\...\Uninstall\Golan.GDM`.
- **الوضع المحمول (Portable Mode):** يعمل تلقائياً عند وجود ملف `GDM.portable` أو `Golan.portable` أو `portable.txt` بجانب `GDM.exe` (أو عبر متغير البيئة `GOLAN_HOME` / `GDM_HOME`)، فيحفظ جميع الإعدادات والبيانات داخل مجلد `Data\` دون كتابة أي مفتاح في سجل ويندوز.
- **تصدير واستيراد القوائم:** يدعم تصدير واستيراد قوائم التنزيل بصيغة JSON (`"gdm-export"`) أو نصية أو EF2.

---

## هيكل المشروع ومسارات البيانات

```text
GDM/
├── src/GDM/                         # تطبيق سطح المكتب (GDM.exe — WPF .NET 10)
│   ├── Core/                        # محرك التنزيل، الإعدادات، التورنت، الفاحص، وفك الضغط
│   ├── Integration/                 # تكامل المتصفحات، المثبّت، المحدّث، سطر الأوامر، وخادم التحكم عن بُعد
│   ├── UI/                          # نوافذ WPF والسمات (UI/Theme/Brand.xaml, Light.xaml, Dark.xaml)
│   ├── Assets/                      # أيقونات وشعارات جولان (gdm.ico, gdm.png, gdm-logo-full.png)
│   └── i18n/                        # جداول الترجمة المدمجة (10 مجالات × لغتين = 1462 مفتاحاً لكل لغة)
├── extension/                       # إضافة المتصفح (Manifest V3) لكروميوم وفايرفوكس + اختبارات Node
├── installer/                       # نص إعداد Inno Setup (gdm.iss) وسكربت البناء (build-setup.ps1)
├── store/                           # وثائق وأصول النشر في متاجر الإضافات (Chrome / Edge / Firefox AMO)
├── tools/                           # أدوات بناء الأصول البصرية وحزم الإضافات (build_brand_assets.py, pack-extension.ps1)
└── tests/
    ├── engine/                      # اختبارات المحرك والأمان والخادم العدائي (EngineTest + Tests_Hard)
    ├── render/                      # أداة العرض غير المرئي وفحص التخطيط البصري (RenderTest — 200 لقطة)
    ├── ui/                          # اختبارات أتمتة الواجهة الرسومية عبر FlaUI/UIA3 (UiTest)
    └── smoke.ps1                    # فحص الدخان الشامل لملف النشر المستقل GDM.exe
```

### مسارات البيانات الافتراضية (في الوضع العادي غير المحمول)
- **ملف البرنامج المثبّت:** `%LOCALAPPDATA%\Programs\Golan\GDM\GDM.exe`
- **بيانات وإعدادات المستخدم (`AppPaths.DataDir`):** `%APPDATA%\Golan\GDM` (`settings.json`، `downloads.json`، `stats.json`)
- **البيانات المحلية والسجلات (`AppPaths.LocalDir`):** `%LOCALAPPDATA%\Golan\GDM` (`log.txt`، `extension\`، `native\com.golan.gdm.json`، `native\com.golan.gdm.firefox.json`، `gdm-firefox.xpi`)

---

## متطلبات البناء والتشغيل

1. **.NET 10 SDK** (`10.0.100` أو أحدث) على نظام ويندوز 10/11 (x64).
2. **Node.js** (الإصدار 20 أو أحدث) لتشغيل اختبارات إضافة المتصفح.
3. **Python 3.10+** لتشغيل خوادم الاختبار المحلية (`testserver.py` و`hardserver.py`) وتوليد الأصول البصرية (`build_brand_assets.py`).
4. **Inno Setup 6** (اختياري) لبناء حزمة المثبّت المستقل `GDMSetup.exe`.

### خطوات البناء من المصدر

```powershell
# 1. بناء تطبيق سطح المكتب (Release)
dotnet build GDM/src/GDM/GDM.csproj -c Release

# 2. نشر ملف تنفيذي مستقل بملف واحد (Self-Contained Single-File GDM.exe)
dotnet publish GDM/src/GDM/GDM.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish

# 3. بناء حزم متاجر الإضافات (Chrome/Edge ZIP + Firefox XPI)
powershell -ExecutionPolicy Bypass -File GDM/tools/pack-extension.ps1

# 4. بناء مثبّت ويندوز (GDMSetup.exe)
powershell -ExecutionPolicy Bypass -File GDM/installer/build-setup.ps1
```

---

## تثبيت إضافة المتصفح

عند تشغيل `GDM.exe`، يقوم البرنامج تلقائياً باستخراج ملفات الإضافة إلى `%LOCALAPPDATA%\Golan\GDM\extension` وبناء حزمة فايرفوكس `%LOCALAPPDATA%\Golan\GDM\gdm-firefox.xpi` وتسجيل مضيف المراسلة الأصلية `com.golan.gdm`.

- **متصفحات كروميوم (Chrome / Edge / Brave / Vivaldi / Opera):**
  1. افتح صفحة الإضافات (`chrome://extensions` أو `edge://extensions`).
  2. فعّل **وضع المطوّر (Developer mode)**.
  3. اضغط **تحميل إضافة غير محزومة (Load unpacked)** واختر المجلد `%LOCALAPPDATA%\Golan\GDM\extension`.
- **متصفح فايرفوكس (Firefox):**
  راجع الدليل المفصّل في ملف [FIREFOX.md](FIREFOX.md).

---

## خيارات سطر الأوامر (CLI)

يدعم `GDM.exe` المعاملات التالية:

| الأمر | الوظيفة |
|---|---|
| `GDM.exe --help` | عرض ملخص أوامر سطر الأوامر (`add`، `add-file`، `start-queue`، `stop-queue`، `pause-all`، `list`). |
| `GDM.exe --install [--quiet]` | تثبيت جولان للمستخدم الحالي في `%LOCALAPPDATA%\Programs\Golan\GDM\GDM.exe` وتسجيل المتصفحات. |
| `GDM.exe --uninstall [--quiet]` | إلغاء تثبيت جولان وحذف الاختصارات ومفاتيح السجل مع الحفاظ على الملفات المنزّلة. |
| `GDM.exe --pack-extension [outDir]` | بناء حزم الإضافة القياسية (`gdm-2.2.0-chrome-edge.zip` و`gdm-2.2.0-firefox.xpi`). |
| `GDM.exe --background` | تشغيل البرنامج في الخلفية داخل منطقة إشعارات النظام (System Tray). |
| `GDM.exe add "<URL>" [--queue <name>] [--folder <dir>] [--name <file>] [--start\|--later]` | إضافة رابط تنزيل عبر سطر الأوامر مع تحديد المجلد أو الاسم أو قائمة الانتظار. |
| `GDM.exe list [--json]` | عرض قائمة التنزيلات الحالية وحالاتها (نصياً أو بصيغة JSON). |
| `GDM.exe "<URL>"` | إرسال رابط HTTP/HTTPS أو `magnet:` إلى النسخة العاملة (أو فتح نافذة الإضافة). |
| `GDM.exe "<path.torrent>"` | فتح ملف `.torrent` مباشرةً وعرض نافذة اختيار الملفات. |

---

## تشغيل الاختبارات والتحقق الهندسي

جميع الاختبارات مصمّمة لتعمل في بيئة معزولة تماماً عبر متغير البيئة `GOLAN_HOME` / `GDM_HOME` دون المساس بملفات المستخدم الحقيقية أو سجل النظام:

```powershell
# 1. اختبارات إضافة المتصفح (30 اختباراً عبر Node.js test runner)
node --test GDM/extension/tests/

# 2. اختبارات المحرك والأمان والخادم العدائي (EngineTest + Tests_Hard)
# يُشغّل testserver.py و hardserver.py ثم يُنفّذ EngineTest.csproj

# 3. اختبارات العرض البصري وفحص التخطيط (RenderTest — 48 فحص TDD + 200 صورة PNG عبر 4 أنماط، 0 أخطاء تخطيط)
dotnet run --project GDM/tests/render/RenderTest.csproj -c Release

# 4. اختبارات أتمتة الواجهة الرسومية الشاملة (UiTest — 28 فحص UIA3)
dotnet run --project GDM/tests/ui/UiTest.csproj -c Release

# 5. فحص الدخان للملف التنفيذي المنشور (Smoke Test — 8 فحوصات على GDM.exe المنشور)
powershell -ExecutionPolicy Bypass -File GDM/tests/smoke.ps1
```

## ربط جولان بالمتصفح
من «المساعدة ← ربط جولان بالمتصفح» أو «الخيارات ← المتصفحات ← أضف جولان إلى …»: يختار المستخدم المتصفح والبروفايل (تُقرأ بروفايلات الجهاز تلقائيًا)، فيفتح جولان صفحة الإضافات في ذلك البروفايل وينسخ مسار الإضافة، ويتحقق مباشرةً من تفعيل «وضع المطوّر» ومن تثبيت الإضافة واتصالها. لا يسمح كروم وإيدج بتثبيت الإضافات تلقائيًا من أي برنامج، فتبقى ضغطتان إلى أن تُنشر الإضافة في المتاجر.

## الترخيص والتفعيل
- يعمل البرنامج مجانًا بكامل ميزاته لمدة 30 يومًا من أول تشغيل، ثم يطلب مفتاح تفعيل.
- يظهر للمستخدم «رمز الجهاز» في «المساعدة ← التفعيل»؛ يرسل اسمه وهذا الرمز إلى صاحب البرنامج.
- يُصدر صاحب البرنامج المفتاح من «مولّد تراخيص جولان» (`GDMLicenseGenerator.exe`، لا يُوزَّع مع البرنامج): الاسم + رمز الجهاز + المدة (شهر، 3 أشهر، 6 أشهر، سنة، مدى الحياة، أو تاريخ محدد).
- المفتاح موقّع رقميًا ومربوط بالجهاز؛ لا يعمل على جهاز آخر، ولا يمكن تعديل اسمه أو تاريخه. مفتاح التوقيع السري محفوظ مشفّرًا في `%USERPROFILE%\.golan-keys` على جهاز صاحب البرنامج فقط؛ احفظ له نسخة احتياطية من داخل المولّد.

---

## الرخصة · License

جولان برنامج حر ومفتوح المصدر بموجب **رخصة جنو العمومية الإصدار ٣** ([GPL-3.0](LICENSE)). يحق لك استخدامه ودراسته وتعديله وإعادة نشره، بشرط أن تنشر أي نسخة معدّلة بالرخصة نفسها ومع كودها المصدري.
النسخة الجاهزة الرسمية متاحة من [صفحة الإصدارات](https://github.com/dark9guna/golan-download-manager-releases/releases/latest)، وتشمل تجربة ٣٠ يومًا ثم مفتاح تفعيل يدعم استمرار التطوير.

Golan is free software under the **GNU General Public License v3** ([GPL-3.0](LICENSE)): use, study, modify and share it, provided modified versions are released under the same licence with their source. Official builds are on the [releases page](https://github.com/dark9guna/golan-download-manager-releases/releases/latest) (30-day trial, then an activation key that funds development).

Copyright © 2026 Khalid ([@dark9guna](https://github.com/dark9guna)). Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

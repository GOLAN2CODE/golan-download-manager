# دليل إرسال إضافة «جولان — مدير التنزيل» (Golan Download Manager) إلى متاجر المتصفحات

يوضّح هذا الدليل خطوات تجهيز وبناء وإرسال إضافة **جولان — مدير التنزيل (Golan Download Manager — GDM)** الرسمية إلى المتاجر الثلاثة الكبرى: **Chrome Web Store**، ومتجر **Microsoft Edge Add-ons**، ومتجر **Mozilla Add-ons (AMO)** على الموقع `addons.mozilla.org`.

---

## 1. بناء الحزم الجاهزة للنشر والتحقق منها

قبل رفع الإضافة إلى أي متجر، شغّل أداة التحقق والحزم من جذر المشروع:

```powershell
powershell -ExecutionPolicy Bypass -File tools/pack-extension.ps1
```

تقوم هذه الأداة بالخطوات التالية:
1. التحقق من سلامة ملف `manifest.json` ووجود جميع الأيقونات (`16`، `24`، `32`، `48`، `128`) والملفات البرمجية وجداول الترجمة العربية والإنجليزية (`_locales/ar/messages.json` و`_locales/en/messages.json`) وعدم وجود أي مسارات نسبية غير آمنة.
2. إنتاج أرشيفات حتمية متطابقة البايتات (Deterministic ZIP/XPI) في مجلد `dist/store/` ومجلد `store/dist/`:
   - **`gdm-2.2.0-chrome-edge.zip`**: حزمة مخصّصة لمتجر **Chrome Web Store** ومتجر **Microsoft Edge Add-ons** (تستخدم `background.service_worker` وتزيل مفاتيح التطوير المحلية وحقل `browser_specific_settings`).
   - **`gdm-2.2.0-firefox.xpi`**: حزمة مخصّصة لمتجر **Mozilla Add-ons (`addons.mozilla.org`)** (تستخدم `background.scripts` وتحافظ على `browser_specific_settings.gecko.id = "gdm@golan.local"`).

---

## 2. النشر في متجر جوجل كروم (Chrome Web Store)

1. افتح لوحة تحكم مطوّري **Chrome Web Store**: `https://chrome.google.com/webstore/devconsole`
2. اضغط على **عنصر جديد (New Item)** وارفع الملف **`gdm-2.2.0-chrome-edge.zip`**.
3. **تبويب بيانات المتجر (Store Listing):**
   - **اللغة الافتراضية:** العربية (`ar`) — انسخ الاسم والوصف المختصر والوصف التفصيلي من ملف `store/listing.ar.md`.
   - **اللغة الإضافية:** الإنجليزية (`en`) — انسخ النصوص المقابلة من ملف `store/listing.en.md`.
   - **التصنيف:** الإنتاجية (Productivity).
   - **الأصول البصرية:** ارفع الأيقونة `store/icon-300.png` (أو `extension/icons/icon128.png`)، والبطاقة الترويجية الصغيرة `store/promo-small-440x280.png`، والبطاقة العريضة `store/promo-marquee-1400x560.png`، ولقطات الشاشة المذكورة في `store/listing.ar.md`.
4. **تبويب الخصوصية (Privacy Practices):**
   - **الغرض الوحيد (Single Purpose):** التكامل مع برنامج «جولان — مدير التنزيل» المحلي على نظام ويندوز (`com.golan.gdm`) لالتقاط التنزيلات وروابط الوسائط من المتصفح.
   - **مبرّرات الصلاحيات (Permission Justifications):** انسخ مبرّرات كل صلاحية (`nativeMessaging`، `downloads`، `contextMenus`، `cookies`، `storage`، `webRequest`، `tabs`، `scripting`، `notifications`، `host_permissions`) من جدول `store/permissions.md`.
   - **استخدام الكود البعيد (Remote Code):** اختر **لا، أنا لا أستخدم كوداً بعيداً (No, I am not using Remote code)**.
   - **الإفصاح عن جمع البيانات:** أعلن عن عدم جمع أو نقل أي بيانات خارج جهاز المستخدم، وأضف رابط سياسة الخصوصية من `store/privacy-policy.ar.md` و`store/privacy-policy.en.md`.
5. **ملاحظات للمراجعين (Reviewer Notes):**
   وضّح للمراجع أن الإضافة هي أداة مرافقة لتطبيق سطح المكتب **Golan Download Manager (`GDM.exe`)** على ويندوز عبر بروتوكول المراسلة الأصلية `com.golan.gdm`، وأنها في حال عدم وجود التطبيق المحلي تترك التنزيلات للمتصفح بشكل طبيعي دون أي خطأ.
6. **تثبيت معرّف الإضافة (`ExtensionId`):**
   بعد نشر الإضافة في المتجر وحصولها على المعرّف الدائم المكوّن من 32 حرفاً، تأكد من مطابقته للقيمة `BrowserIntegration.ExtensionId` في `BrowserIntegration.cs` ليُدرج تلقائياً في قائمة `allowed_origins` داخل ملف `%LOCALAPPDATA%\Golan\GDM\native\com.golan.gdm.json`.

---

## 3. النشر في متجر مايكروسوفت إيدج (Microsoft Edge Add-ons)

1. افتح مركز شركاء مايكروسوفت (**Partner Center — Edge Add-ons**): `https://partner.microsoft.com/dashboard/microsoftedge/overview`
2. اختر **إنشاء إضافة جديدة (Create new extension)** وارفع الملف نفسه **`gdm-2.2.0-chrome-edge.zip`**.
3. املأ حقول المتجر باللغتين العربية والإنجليزية من `store/listing.ar.md` و`store/listing.en.md`، وارفع الأيقونة `store/icon-300.png` (300×300 بكسل) والبطاقات الترويجية `store/promo-small-440x280.png` و`store/promo-marquee-1400x560.png`.
4. في قسم **ملاحظات الشهادة والاعتماد (Notes for certification)**، الصق جدول الصلاحيات من `store/permissions.md` وبيان الخصوصية من `store/privacy-policy.ar.md`.

---

## 4. النشر والتوقيع في متجر موزيلا فايرفوكس (`addons.mozilla.org`)

1. افتح مركز مطوّري الإضافات في موزيلا (**AMO Developer Hub**): `https://addons.mozilla.org/developers/addon/submit/`
2. اختر طريقة التوزيع:
   - **إدراج في المتجر العام (On this site)** لنشر الإضافة في دليل فايرفوكس العام.
   - أو **توزيع ذاتي (On your own)** للحصول على ملف `.xpi` موقّع رقمياً من موزيلا يمكن تثبيته مباشرةً داخل `GDM.exe` في جميع نسخ فايرفوكس القياسية.
3. ارفع الملف **`gdm-2.2.0-firefox.xpi`**.
4. عند السؤال **«هل تتطلب إضافتك إرسال كود مصدري؟»** اختر **لا (No)** لأن جميع ملفات JavaScript (`background.js`، `content.js`، `popup.js`) مكتوبة بصيغة نصية مقروءة مباشرةً دون تصغير (Minification) أو تشويش (Obfuscation) أو أدوات تجميع خارجية.
5. انسخ الوصف العربي والإنجليزي من `store/listing.ar.md` و`store/listing.en.md`، ومبرّرات الصلاحيات من `store/permissions.md`، وسياسة الخصوصية من `store/privacy-policy.ar.md`.
6. بعد اعتماد الحزمة وتوقيعها من `addons.mozilla.org`، تأكد من أن معرّف الإضافة يظل `gdm@golan.local` ليتطابق مع `BrowserIntegration.FirefoxExtensionId` في ملف `%LOCALAPPDATA%\Golan\GDM\native\com.golan.gdm.firefox.json`.

# تثبيت إضافة متصفح فايرفوكس لبرنامج جولان (Golan Download Manager)

يدعم برنامج **جولان — مدير التنزيل (GDM)** متصفح **Mozilla Firefox** بشكل كامل عبر حزمة الإضافة المدمجة (`.xpi`) المتوافقة مع معيار **Manifest V3 / WebExtensions** وبروتوكول المراسلة الأصلية (**Native Messaging**).

---

## 1. كيف يجهّز «جولان» ملفات فايرفوكس تلقائياً؟

عند تشغيل `GDM.exe` (أو عند تنفيذ أمر التثبيت `GDM.exe --install`)، يقوم البرنامج تلقائياً بالخطوات التالية دون الحاجة إلى صلاحيات مسؤول النظام:

1. **إنشاء حزمة الإضافة (`gdm-firefox.xpi`):**
   يبني البرنامج حزمة فايرفوكس الجاهزة في المسار:
   ```text
   %LOCALAPPDATA%\Golan\GDM\gdm-firefox.xpi
   ```
   كما يمكنك توليد حزم المتاجر القياسية (`gdm-2.2.0-firefox.xpi` و`gdm-2.2.0-chrome-edge.zip`) في أي وقت عبر الأداة:
   ```powershell
   powershell -ExecutionPolicy Bypass -File GDM\tools\pack-extension.ps1
   ```
   أو من واجهة البرنامج عبر القائمة: **التعليمات ← بناء حزم المتصفحات…**.

2. **كتابة ملف تعريف المضيف الأصلي (Native Messaging Host Manifest):**
   يكتب البرنامج ملف التعريف الخاص بفايرفوكس في المسار:
   ```text
   %LOCALAPPDATA%\Golan\GDM\native\com.golan.gdm.firefox.json
   ```
   ويحتوي على معرّف الإضافة الرسمي `gdm@golan.local` ومسار الملف التنفيذي `GDM.exe`.

3. **تسجيل المضيف في سجل ويندوز للمستخدم الحالي (`HKCU`):**
   يسجّل البرنامج المفتاح التالي:
   ```text
   HKCU\Software\Mozilla\NativeMessagingHosts\com.golan.gdm
   ```
   ليشير مباشرةً إلى ملف `com.golan.gdm.firefox.json`.

---

## 2. طرق تثبيت الإضافة في فايرفوكس

### الطريقة الأولى: التثبيت المباشر من نافذة الترحيب في جولان (للنسخة الموقّعة من متجر Mozilla AMO)
1. افتح برنامج **جولان (`GDM.exe`)**.
2. من شريط القوائم العلوي، افتح **المساعدة ← طريقة تثبيت إضافة المتصفح…**.
3. اضغط على زر **تثبيت في فايرفوكس** أو اسحب ملف `gdm-firefox.xpi` إلى نافذة فايرفوكس ووافق على الصلاحيات.

### الطريقة الثانية: التثبيت المؤقت للمطوّرين والاختبار المحلي (`about:debugging`)
في إصدارات فايرفوكس القياسية (Firefox Release) قبل توقيع الحزمة من متجر Mozilla Add-ons، يمكنك تحميل الإضافة فوراً عبر لوحة تصحيح الأخطاء:
1. افتح متصفح فايرفوكس واكتب في شريط العناوين:
   ```text
   about:debugging#/runtime/this-firefox
   ```
2. اضغط على زر **تحميل إضافة مؤقتة… (Load Temporary Add-on…)**.
3. اختر الملف `%LOCALAPPDATA%\Golan\GDM\gdm-firefox.xpi` (أو ملف `manifest.json` داخل `%LOCALAPPDATA%\Golan\GDM\extension`).

### الطريقة الثالثة: التثبيت الدائم لحزمة غير موقّعة في Firefox Developer Edition / Nightly / ESR
إذا كنت تستخدم نسخة **Firefox Developer Edition** أو **Firefox Nightly** أو **Firefox ESR**:
1. افتح `about:config` في شريط العناوين واضبط الخيار التالي على `false`:
   ```text
   xpinstall.signatures.required = false
   ```
2. افتح `about:addons`، واضغط على أيقونة الترس أعلى الصفحة، ثم اختر **تثبيت إضافة من ملف… (Install Add-on From File…)**.
3. اختر الملف:
   ```text
   %LOCALAPPDATA%\Golan\GDM\gdm-firefox.xpi
   ```

---

## 3. التحقق من نجاح الاتصال واستكشاف الأخطاء

1. تأكد من أن برنامج **جولان (`GDM.exe`)** يعمل في سطح المكتب أو في منطقة الساعة (System Tray).
2. انقر على أيقونة **جولان** في شريط أدوات فايرفوكس؛ يجب أن تظهر شارة الحالة باللون الأخضر **«متصل» (Connected)**.
3. إذا ظهرت الحالة **«غير متصل» (Disconnected)**:
   - افتح في جولان: **الخيارات ← المتصفحات** واضغط على زر **إعادة تسجيل تكامل المتصفحات الآن** (أو نفّذ الأمر `GDM.exe --install --quiet`).
   - تأكد من أن المفتاح `HKCU\Software\Mozilla\NativeMessagingHosts\com.golan.gdm` يشير إلى ملف موجود فعلياً، وأن الحقل `"path"` داخل ملف `com.golan.gdm.firefox.json` يشير إلى المسار الصحيح لملف `GDM.exe`.
   - تأكد من أن معرّف الإضافة في `manifest.json` هو `gdm@golan.local` ومطابق للقيمة الموجودة في قائمة `allowed_extensions` داخل ملف `com.golan.gdm.firefox.json`.

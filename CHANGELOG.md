# سجل التغييرات · Changelog

## 2.6.1 — 2026-11-19
- التثبيت التلقائي لإضافة المتصفح صار يعمل فعلًا: يطلب من المتصفح كشف صفحة الإضافات، ويفتحها في نافذة خاصة، ولا يلمس أي نافذة أخرى · The automatic extension installer now really completes: it asks the browser to expose the extensions page, opens it in a window of its own, and never touches another window.
- يبحث أولًا عن المتصفحات وملفاتها الشخصية ويعرضها بحالتها، ثم يثبّت الإضافة فقط في الملفات التي تختارها · It first lists every browser profile with its state, then installs only into the profiles you tick.
- يتجاهل نسخ «chrome.exe» التابعة لبرامج أخرى، ويعامل Chrome Beta وDev وCanary كمتصفحات مستقلة بملفاتها · Copies of chrome.exe that belong to other programs are ignored; Chrome Beta, Dev and Canary are separate browsers with their own profiles.
- إضافة محمّلة من مجلد قديم أو محذوف تُعرض «تحتاج إلى إعادة تثبيت» ويُعاد تحميلها من مجلد جولان · An extension loaded from an old or deleted folder is offered for reinstalling and is loaded again from Golan’s own folder.
- خطوات التثبيت التلقائي تُكتب في السجل لمعرفة أين توقف · Auto-install steps are written to the log to show where a run stopped.

## 2.6.0 — 2026-11-19
- تثبيت تلقائي لإضافة المتصفح بضغطة واحدة (من نافذة التشغيل الأول، أو «المساعدة»، أو «الخيارات»، أو «دليل المتصفح») لمتصفحات كروميوم وفايرفوكس · One-click automatic browser extension installer (first-run offer, Help menu, Options, and Browser Guide) for Chromium browsers and Firefox.
- تنزيل مباشر لبث HLS (بما يشمل فك تشفير AES-128 ومقاطع fMP4) وMPEG-DASH، مع كشف محتوى DRM وتنبيه واضح وزر تحديث أداة الفيديو · Direct HLS (including AES-128 decryption and fMP4) and MPEG-DASH downloading, DRM detection, and an in-window video tool update button.
- دعم بروتوكول FTP/FTPS مع الاستئناف، ودعم روابط البيانات (`data:`) · Support for FTP/FTPS downloads with resume and `data:` URLs.
- محلّلات روابط جديدة لمواقع GoFile وPixeldrain وSourceForge ومنشورات Telegram العامة وتصدير مستندات Google Docs/Sheets/Slides · New link resolvers for GoFile, Pixeldrain, SourceForge, public Telegram posts, and Google Docs/Sheets/Slides exports.
- تحسينات شاملة لإضافة المتصفح: استقرار التقاط التنزيلات في Manifest V3، التقاط الفيديو المدمج وروابط `magnet:`، وحالة الاتصال مع زر «إعادة المحاولة» · Browser extension fixes: reliable MV3 capture, embedded video sniffing, magnet links, and popup offline status with a Retry button.
- تحسينات الواجهة في جميع النوافذ: رسائل خطأ واضحة بالعربية والإنجليزية، حالات فارغة إرشادية، وضبط محاذاة RTL/LTR واكتمال الترجمة · UI improvements across all windows: plain-language errors, helpful empty states, RTL/LTR polish, and verified AR/EN translation parity.
- مراجعة أمان وثبات شاملة: التثبيت التلقائي لا يكتب في نافذة أو تبويب غير تابع له، إعادة محاولة قطع البث المنقطعة، رفض دمج فيديو ناقص، وعدم ظهور كلمة سر FTP في السجل أو خصائص الملف · Safety and stability review: auto-install never types into a window or tab it does not own, stream pieces retry, incomplete video merges are refused, and FTP passwords no longer reach the log or file properties.
- FTP/FTPS: رفض قناة بيانات غير مشفّرة مع FTPS، إيقاف البيانات الزائدة عن الحجم المعلن، واستئناف أدق · FTP/FTPS: no cleartext data channel on FTPS, extra bytes beyond the declared size are refused, more reliable resume.
- روابط GoFile وPixeldrain وTelegram وSourceForge تتابع بعد تغيّر الملف على الخادم، وقائمة المزامنة لا تعيد تنزيل البث وFTP بلا سبب · Resolver links recover after a server-side change; the sync queue no longer re-downloads streams and FTP items.
- إضافة المتصفح: لا إرسال مزدوج للتنزيل، لا تنزيل يبقى موقوفًا بعد إعادة تشغيل الإضافة، ودعم Firefox من الإصدار 121 · Extension: no double hand-off, no download left paused after a restart, Firefox 121+.
- تنبيه عند تجاوز 10,000 رابط في التنزيل الجماعي · A notice when a batch exceeds 10,000 links.
- استئناف تنزيل البث المباشر (HLS وDASH) من آخر قطعة مكتملة بدل البدء من جديد · Direct HLS/DASH downloads resume from the last finished piece instead of starting over.
- قطع البث الكبيرة (أكثر من 32 ميغابايت وحتى 8 غيغابايت) تمر عبر القرص بدل الذاكرة · Large stream pieces (over 32 MB, up to 8 GB) go through the disk instead of memory.
- حفظ صفحة الويب نفسها عند إضافة رابط ‎.html يدويًا، مع بقاء الرفض للروابط الملتقطة من المتصفح · A manually added .html link saves the page itself; browser-captured links are still refused.
- التشغيل الأول: نافذة ترحيب أقصر مع زر لتبديل اللغة، وتظهر دائمًا مرة واحدة قبل عرض تثبيت الإضافة · First run: a shorter Welcome window with a language switch, always shown once before the extension offer.
- تجربة الاستخدام: نافذة واحدة بعد اكتمال التنزيل، رسالة خطأ واضحة في نافذة التقدّم، خيارات متقدمة مطوية في نافذة الإضافة، عنوان مخطط السرعة بالعربية، وتوحيد مصطلحي «قائمة الانتظار» و«التنزيل» · Usability: one window after completion, a clear error in the progress window, advanced options folded in the Add window, an Arabic speed-graph header, and consistent wording.

## 2.5.1 — 2026-10-29
- تحديث تلقائي: يتحقق جولان مرة في اليوم من صفحة الإصدارات الرسمية ويحدّث نفسه بعد التحقق من البصمة، ويمكن إطفاؤه من الخيارات · Automatic updates from the official releases page (SHA-256 verified), can be turned off in Options.
- إصلاح: الإيقاف المؤقت في نفس لحظة انتهاء التنزيل لم يعد يسبب خطأ · Fix: pausing at the moment a download ends no longer throws.

## 2.5.0 — 2026-10-29
- التقاط التنزيل من المتصفح فورًا قبل أن يبدأه المتصفح، عبر اتصال دائم مع الإضافة · Browser downloads are taken before the browser shows them, over one open native port.
- تعامل ذكي مع المواقع المحمية (Cloudflare): اتصالات أقل للملفات الصغيرة، وانتظار تلقائي ثم متابعة · Smarter with rate-limited sites: fewer connections for small files, automatic wait-and-continue.
- التحقق من الملف قبل كل استئناف، والمتابعة عبر المرايا وخوادم التوزيع · Every resume verifies the file and continues across mirrors and CDN nodes.
- لا يحفظ صفحة ويب مكان الملف، ويتابع مرايا http العامة · Never saves a web page as the file; follows public http mirrors.
- «تحديث عنوان التنزيل» عند انتهاء صلاحية الرابط · "Refresh download address" for expired links.
- فيديو: قائمة جودة أسرع، أجزاء متوازية ليوتيوب، الوضع الذكي · Video: faster quality list, parallel YouTube pieces, Smart mode.
- رخصة GPL-3.0 والكود مفتوح · Open source under GPL-3.0.

## 2.4.0
- نظام التفعيل (تجربة ٣٠ يومًا) ودليل إضافة المتصفح · Licensing (30-day trial) and the browser setup guide.

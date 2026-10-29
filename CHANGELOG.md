# سجل التغييرات · Changelog

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

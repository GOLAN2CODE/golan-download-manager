# سياسة الأمان · Security Policy

## الإصدارات المدعومة · Supported versions

نصلح الثغرات في آخر إصدار فقط. Security fixes go into the latest release only.

| الإصدار · Version | مدعوم · Supported |
|---|---|
| 2.5.x | ✅ |
| أقدم · older | ❌ |

## الإبلاغ عن ثغرة · Reporting a vulnerability

لا تفتح مشكلة علنية للثغرات. استخدم **[الإبلاغ الخاص عن الثغرات](https://github.com/dark9guna/golan-download-manager/security/advisories/new)** في غيت هاب، مع خطوات إعادة المشكلة ونسخة جولان ونسخة ويندوز.
Please do not open a public issue. Use **[private vulnerability reporting](https://github.com/dark9guna/golan-download-manager/security/advisories/new)** with steps to reproduce, your Golan version and Windows version.

سنرد خلال ٧ أيام، ونصدر الإصلاح ثم ننشر التفاصيل بعد توفره للمستخدمين.
We aim to answer within 7 days, ship a fix, and publish details once users can update.

## ما يهمنا بشكل خاص · Areas of special interest
- تنزيل ملفات إلى خارج المجلد المختار، أو الكتابة فوق ملفات النظام · writing outside the chosen folder
- تسريب ملفات تعريف الارتباط أو كلمات المرور إلى موقع آخر · leaking cookies or logins to another host
- رسائل إضافة المتصفح أو قناة الاتصال المحلية · the browser extension and the local native-messaging bridge
- التحكم عن بعد عبر الشبكة المحلية · the LAN remote-control server
- التحديث التلقائي · the self-updater

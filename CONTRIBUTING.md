# المساهمة في جولان · Contributing to Golan

شكرًا لاهتمامك! المساهمات مرحّب فيها: إصلاح أخطاء، تحسين الأداء، ترجمات، أو مزايا جديدة.
Thanks for your interest! Bug fixes, performance work, translations and new features are welcome.

## قبل ما تبلّش · Before you start
- للأخطاء: افتح [مشكلة](https://github.com/dark9guna/golan-download-manager/issues) فيها خطوات إعادة الخطأ، ونسخة ويندوز، والسطور المهمة من `gdm.log` (بدون روابط أو كوكيز خاصة).
- للمزايا الكبيرة: اقترحها بمشكلة أولًا لنتفق على الطريقة.
- For bugs, open an issue with steps to reproduce, your Windows version and the relevant lines of `gdm.log` (no private links or cookies). Discuss large features in an issue first.

## البناء والاختبار · Build and test
Requires the .NET 10 SDK, Node.js (extension tests) and Python 3 (test servers). See **README.md** for details.

```powershell
dotnet build src/GDM/GDM.csproj -c Release
node --test extension/tests/
powershell -File tests/smoke.ps1
```

Engine tests (isolated, never touch your real settings):

```powershell
cd tests/engine
dotnet build EngineTest.csproj -c Release -o bin
python -I testserver.py served 18971   # in another terminal
dotnet bin/EngineTest.dll served out 18971
```

## قواعد · Guidelines
- كل تغيير بيجي مع اختبار بيثبته، وكل الاختبارات لازم تنجح.
- كل نص بيشوفه المستخدم بيكون بملفات `src/GDM/i18n/*.ar.json` و`*.en.json` معًا.
- ما في أسرار أو مفاتيح بالكود أبدًا.
- Every change comes with a test that proves it; all tests must pass. User-visible text lives in both `i18n/*.ar.json` and `*.en.json`. Never commit secrets or keys.

By contributing you agree that your work is released under the [GPL-3.0](LICENSE).

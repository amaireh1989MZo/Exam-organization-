# بنك الأسئلة الداخلي

تطبيق ويب داخلي يجرد بنك أسئلة المؤسسة على مجلد الشبكة، ويستخرج الأسئلة ويربطها بأطر الكفايات المعتمدة، ثم يصدّرها إلى Moodle بصيغتي GIFT وMoodle XML. كل المعالجة داخل شبكة المؤسسة.

- **ابدأ من هنا:** [`docs/PROJECT_OVERVIEW_AR.md`](docs/PROJECT_OVERVIEW_AR.md)
- **الأساس والجرد:** [`docs/INVENTORY_AR.md`](docs/INVENTORY_AR.md)
- **الاستخراج والرفع:** [`docs/EXTRACTION_AR.md`](docs/EXTRACTION_AR.md)
- **المراجعة وسجل التدقيق:** [`docs/REVIEW_AR.md`](docs/REVIEW_AR.md)
- **التصدير إلى Moodle:** [`docs/EXPORT_AR.md`](docs/EXPORT_AR.md)
- **التكرار وZIP وتقرير Excel:** [`docs/QUALITY_AR.md`](docs/QUALITY_AR.md)
- **أطر الكفايات والتصنيف:** [`docs/CLASSIFICATION_AR.md`](docs/CLASSIFICATION_AR.md)
- **الأفكار والخطة التفصيلية:** [`docs/interface/PROPOSAL_AR.md`](docs/interface/PROPOSAL_AR.md)
- **النموذج التفاعلي للواجهة:** [`docs/interface/prototype.html`](docs/interface/prototype.html)

- **النشر على Windows Server وIIS:** [`deploy/README_AR.md`](deploy/README_AR.md)

## التشغيل على جهازك للتجربة

1. ثبّت **.NET 10 SDK** من موقع Microsoft (مرة واحدة).
2. انقر مرتين على `run-local.cmd`.
3. اكتب مسار مجلد ملفات الأسئلة عند السؤال، أو اتركه فارغًا واستخدم الرفع بالسحب.
4. يُفتح المتصفح على `http://localhost:5080`.

بعدها: استورد الإطار من شاشة «التصنيف»، وارفع ملفات الأسئلة، ثم وزّع الأسئلة على المؤشرات.

- التشغيل المحلي لمستخدم واحد، ولا يقبل اتصالًا من أجهزة أخرى.
- النتائج وقاعدة البيانات في مجلد `output` بجانب الملف، وملفات الأسئلة لا تُعدَّل.
- هذا الوضع للتجربة فقط. التشغيل لعدة مستخدمين يكون على خادم IIS حسب [`deploy/README_AR.md`](deploy/README_AR.md).

## البناء والاختبار

يتطلب .NET SDK 10.

```powershell
dotnet build
dotnet test
```

> الحالة: نسخة تطوير مختبَرة على بيانات اصطناعية. **غير جاهزة للإنتاج.**

# بنك الأسئلة الداخلي

تطبيق ويب داخلي يجرد بنك أسئلة المؤسسة على مجلد الشبكة، ويستخرج الأسئلة ويربطها بأطر الكفايات المعتمدة، ثم يصدّرها إلى Moodle بصيغتي GIFT وMoodle XML. كل المعالجة داخل شبكة المؤسسة.

- **ابدأ من هنا:** [`docs/PROJECT_OVERVIEW_AR.md`](docs/PROJECT_OVERVIEW_AR.md)
- **المنفَّذ حاليًا (الأساس والجرد):** [`docs/INVENTORY_AR.md`](docs/INVENTORY_AR.md)
- **الأفكار والخطة التفصيلية:** [`docs/interface/PROPOSAL_AR.md`](docs/interface/PROPOSAL_AR.md)
- **النموذج التفاعلي للواجهة:** [`docs/interface/prototype.html`](docs/interface/prototype.html)

## البناء والاختبار

يتطلب .NET SDK 10.

```powershell
dotnet build
dotnet test
```

> الحالة: نسخة تطوير مختبَرة على بيانات اصطناعية. **غير جاهزة للإنتاج.**

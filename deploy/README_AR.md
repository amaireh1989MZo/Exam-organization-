# النشر على Windows Server وIIS

> **تنبيه:** هذه الملفات **لم تُختبر على Windows Server أو IIS**، لأن بيئة التطوير Linux. التطبيق نفسه مختبَر (168 اختبارًا، والأمن على Kestrel حقيقي). جرّب النشر أولًا على خادم تجريبي ومجلد غير سري.

## الملفات
| الملف | الغرض |
|---|---|
| `src/QuestionBank.Web/web.config` | IIS: Windows Authentication، وتعطيل الدخول المجهول، وحد حجم الطلب. ترويسات الأمان (CSP وغيرها) يضيفها التطبيق نفسه |
| `deploy/publish.ps1` | بناء، ثم اختبار، ثم نشر، مع صفحة «يجري التحديث» أثناء النسخ. لا يستبدل إعدادات الإنتاج ولا البيانات |
| `deploy/appsettings.Production.example.json` | مثال لإعدادات الإنتاج: يُنسخ إلى مجلد النشر باسم `appsettings.Production.json` ويُعدَّل |

## الخطوات
1. **المتطلبات على الخادم:**
   - ASP.NET Core **Hosting Bundle 10** من موقع Microsoft الرسمي.
   - IIS مع خاصية **Windows Authentication** (Server Manager ← Web Server ← Security).
2. **حساب الخدمة:** gMSA أو حساب مخصص بالصلاحيات التالية:
   - **قراءة فقط** على مشاركة المصدر، على مستوى NTFS والمشاركة معًا.
   - **تعديل** على مجلد النتائج.
   - **تعديل** على مجلد قاعدة البيانات المحلي (مثل `D:\QuestionBankData`). **لا تضع SQLite على مشاركة شبكة.**
3. **إنشاء الموقع في IIS:**
   - Application Pool بـ «No Managed Code»، وهويته حساب الخدمة.
   - ربط HTTPS بشهادة داخلية، واسم مثل `qbank.corp.local`.
4. **المصادقة:** إذا كانت إعدادات `<authentication>` في `web.config` مقفلة على مستوى الخادم، فاضبطها من IIS Manager ← الموقع ← Authentication:
   - Windows Authentication: **Enabled**.
   - Anonymous Authentication: **Disabled**.
5. **النشر:** `.\deploy\publish.ps1 -Target C:\Apps\QuestionBank\Web`
6. **الإعدادات:** انسخ `appsettings.Production.example.json` إلى مجلد النشر باسم `appsettings.Production.json`، ثم عدّل:
   - المسارات.
   - **أسماء مجموعات AD الحقيقية.**
   - `AllowedHosts`.
7. **التحقق:**
   - `https://qbank.corp.local/health` يعيد `healthy` بدون دخول.
   - الصفحة الرئيسية تفتح بحسابك تلقائيًا، وتظهر أدوارك أسفل القائمة.
   - **مستخدم ليس في أي مجموعة يجب أن يُرفض.**
   - `/api/config/paths` بحساب المدير: كل القيم `true` ولا مشاكل.
8. **أول جرد:** على مجلد تجريبي صغير أولًا، ثم قارن العدد يدويًا (راجع `docs/INVENTORY_AR.md` القسم 9).

## خدمة العامل المنفصلة (موصى بها للجرد الكامل)
العاملان (الجرد والاستخراج) يعملان افتراضيًا داخل الموقع. إعادة تدوير IIS توقفهما مؤقتًا، ثم يُستأنفان دون فقد. أما للجرد الكامل الطويل، فالأفضل خدمة Windows مستقلة:

1. **النشر:** `.\deploy\publish.ps1 -Target C:\Apps\QuestionBank\Web -WorkerTarget C:\Apps\QuestionBank\Worker`
2. **الإعدادات:** انسخ `appsettings.Production.json` نفسه إلى مجلد العامل. يجب أن يشير إلى **ملف القاعدة نفسه** وجذري المصدر والنتائج نفسيهما.
3. **التثبيت** مرة واحدة، من PowerShell بصلاحية مدير:
   ```powershell
   sc.exe create "QuestionBank Worker" binPath= "C:\Apps\QuestionBank\Worker\QuestionBank.Worker.exe" start= delayed-auto obj= "DOMAIN\qb-svc$"
   sc.exe failure "QuestionBank Worker" reset= 86400 actions= restart/60000/restart/60000/restart/60000
   Start-Service "QuestionBank Worker"
   ```
   الحساب `DOMAIN\qb-svc$` هو حساب gMSA نفسه، بالصلاحيات نفسها: قراءة فقط على المصدر.
4. **في الموقع:** `"QuestionBank": { "EnableWorker": false }` حتى لا يعمل عاملان معًا.
5. **التحقق:** لوحة الحالة في الموقع تعرض حالة العامل من نبضه في قاعدة البيانات. إذا توقفت الخدمة تظهر رسالة «عامل المعالجة متوقف» خلال 45 ثانية.

**كيف تصل الطلبات للخدمة:** زر «بدء الجرد» وطلبات المجلدات تُكتب في قاعدة البيانات. الخدمة تفحص طلبات الجرد كل ثانيتين، وطابور الاستخراج كل 10 ثوانٍ.

## ملاحظات مهمة
- **إذا بقي العاملان داخل الموقع:** اضبط **Idle Time-out = 0** و**Start Mode = AlwaysRunning**، وفعّل **Preload Enabled** على الموقع.
- **حد الرفع موجود في مكانين:** `maxAllowedContentLength` في `web.config`، و`MaxFileSizeMb` في الإعدادات. يجب رفعهما معًا.
- **سياسة أمان المحتوى (CSP)** يضيفها التطبيق لكل استجابة، وتمنع أي سكربت أو خط أو اتصال خارجي. اختُبرت في المتصفح: كل الشاشات تعمل تحتها دون أي مخالفة.
- **النسخ الاحتياطي** لملف القاعدة يكون مع إيقاف الموقع، أو بأمر `VACUUM INTO` في SQLite. وينسخ معه مجلد النتائج (الرفع، والتصدير، والتقارير).

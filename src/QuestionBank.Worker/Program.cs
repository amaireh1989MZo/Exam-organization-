using QuestionBank.Core.Hosting;

// خدمة Windows لعاملَي الجرد والاستخراج، منفصلة عن الموقع حتى لا توقفها إعادة تدوير IIS.
// تقرأ الإعدادات نفسها (appsettings.json وappsettings.Production.json ومتغيرات البيئة QuestionBank__...).
// في الموقع: QuestionBank:EnableWorker=false حتى لا يعمل عاملان معًا.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "QuestionBank Worker");
builder.Services.AddQuestionBankCore(builder.Configuration);
builder.Services.AddQuestionBankWorkers();

var host = builder.Build();
await ServiceRegistration.InitializeDatabaseAsync(host.Services, builder.Configuration);
await host.RunAsync();

public partial class Program;

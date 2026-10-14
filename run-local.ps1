# تشغيل بنك الأسئلة على هذا الجهاز للتجربة (مستخدم واحد، بلا خادم ولا IIS).
# - يعمل على http://localhost:5080 فقط، ولا يقبل اتصالًا من أجهزة أخرى.
# - وضع التطوير: الدخول باسم DEV\developer بكل الصلاحيات. لا يُستخدم للإنتاج.
# - مجلد الأسئلة يُقرأ فقط ولا يُعدَّل. النتائج وقاعدة البيانات في مجلد output بجانب هذا الملف.
param(
    [string]$SourceRoot = "",
    [int]$Port = 5080
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "لم يُعثر على .NET. ثبّت .NET 10 SDK من https://dotnet.microsoft.com/download ثم أعد التشغيل." -ForegroundColor Red
    exit 1
}

$output = Join-Path $root "output"
New-Item -ItemType Directory -Force -Path $output | Out-Null
if ($SourceRoot -eq "") {
    $SourceRoot = Read-Host "مسار مجلد ملفات الأسئلة (للقراءة فقط). اتركه فارغًا لاستخدام الرفع بالسحب فقط"
}
if ($SourceRoot -ne "" -and -not (Test-Path $SourceRoot -PathType Container)) {
    Write-Host "المجلد غير موجود: $SourceRoot" -ForegroundColor Red
    exit 1
}
if ($SourceRoot -eq "") { $SourceRoot = Join-Path $root "sample-source"; New-Item -ItemType Directory -Force -Path $SourceRoot | Out-Null }

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:$Port"
$env:QuestionBank__SourceRoot = (Resolve-Path $SourceRoot).Path
$env:QuestionBank__OutputRoot = $output
$env:ConnectionStrings__QuestionBank = "Data Source=$(Join-Path $output 'questionbank.db')"

Write-Host "يبدأ التشغيل... افتح http://localhost:$Port إن لم يُفتح المتصفح تلقائيًا. للإيقاف: Ctrl+C" -ForegroundColor Green
Start-Job -ScriptBlock { param($u) Start-Sleep 8; Start-Process $u } -ArgumentList "http://localhost:$Port" | Out-Null
dotnet run --project (Join-Path $root "src\QuestionBank.Web") -c Release

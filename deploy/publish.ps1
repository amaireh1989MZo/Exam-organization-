<#
.SYNOPSIS
  ينشر بنك الأسئلة الداخلي إلى مجلد على الخادم، بعد البناء وتشغيل الاختبارات.
.EXAMPLE
  .\deploy\publish.ps1 -Target C:\Apps\QuestionBank\Web
.EXAMPLE
  .\deploy\publish.ps1 -Target C:\Apps\QuestionBank\Web -WorkerTarget C:\Apps\QuestionBank\Worker
#>
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$WorkerTarget,
    [string]$WorkerServiceName = 'QuestionBank Worker',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $SkipTests) {
    Write-Host 'تشغيل الاختبارات...'
    dotnet test "$root" --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'فشلت الاختبارات؛ أُلغي النشر.' }
}

$staging = Join-Path $env:TEMP ("qb-publish-" + [guid]::NewGuid().ToString('N'))
dotnet publish "$root\src\QuestionBank.Web" --configuration Release --output $staging
if ($LASTEXITCODE -ne 0) { throw 'فشل النشر.' }

# لا ننشر إعدادات التطوير، ولا نستبدل إعدادات الإنتاج أو البيانات الموجودة على الخادم.
Remove-Item (Join-Path $staging 'appsettings.Development.json') -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Target | Out-Null
$offline = Join-Path $Target 'app_offline.htm'
Set-Content -Path $offline -Value '<p dir="rtl">يجري تحديث بنك الأسئلة. أعد المحاولة بعد دقيقة.</p>' -Encoding UTF8
try {
    Start-Sleep -Seconds 3
    robocopy $staging $Target /E /XF appsettings.Production.json /XD App_Data logs /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "فشل النسخ (robocopy $LASTEXITCODE)." }
}
finally {
    Remove-Item $offline -ErrorAction SilentlyContinue
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "تم النشر إلى $Target"

if ($WorkerTarget) {
    # خدمة العامل: تُوقف، وتُستبدل ملفاتها، ثم تُشغَّل. الجرد يُستأنف من حيث توقف.
    $wstaging = Join-Path $env:TEMP ("qb-worker-" + [guid]::NewGuid().ToString('N'))
    dotnet publish "$root\src\QuestionBank.Worker" --configuration Release --output $wstaging
    if ($LASTEXITCODE -ne 0) { throw 'فشل نشر خدمة العامل.' }
    Remove-Item (Join-Path $wstaging 'appsettings.Development.json') -ErrorAction SilentlyContinue
    $svc = Get-Service -Name $WorkerServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') { Stop-Service -Name $WorkerServiceName; $svc.WaitForStatus('Stopped', '00:01:00') }
    New-Item -ItemType Directory -Force -Path $WorkerTarget | Out-Null
    robocopy $wstaging $WorkerTarget /E /XF appsettings.Production.json /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "فشل نسخ خدمة العامل (robocopy $LASTEXITCODE)." }
    Remove-Item $wstaging -Recurse -Force -ErrorAction SilentlyContinue
    if ($svc) { Start-Service -Name $WorkerServiceName } else { Write-Host "الخدمة غير مثبتة بعد؛ راجع deploy/README_AR.md لتثبيتها." }
    Write-Host "تم نشر خدمة العامل إلى $WorkerTarget"
}

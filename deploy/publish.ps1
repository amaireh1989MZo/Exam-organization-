<#
.SYNOPSIS
  ينشر بنك الأسئلة الداخلي إلى مجلد على الخادم، بعد البناء وتشغيل الاختبارات.
.EXAMPLE
  .\deploy\publish.ps1 -Target C:\Apps\QuestionBank\Web
#>
param(
    [Parameter(Mandatory = $true)][string]$Target,
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

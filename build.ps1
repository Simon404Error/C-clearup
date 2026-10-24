# 一键构建 C盘清理工具.exe
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
& $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ `
      /win32icon:src\app.ico /win32manifest:src\app.manifest `
      /out:"C盘清理工具.exe" /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
      src\CleanC.cs
if ($LASTEXITCODE -eq 0) { Write-Host "构建完成: C盘清理工具.exe" -ForegroundColor Green }

@echo off
:: 一键把 NotepadReplacer.cs 编译成带图标的独立 exe（使用系统自带 csc，无需安装 VS）
set "DIR=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%DIR%\csc.exe" set "DIR=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
"%DIR%\csc.exe" /target:winexe /win32icon:app.ico /win32manifest:app.manifest ^
  /r:"%DIR%\System.Windows.Forms.dll" /r:"%DIR%\System.Drawing.dll" ^
  /out:NotepadReplacer.exe NotepadReplacer.cs
echo.
echo 编译完成。若上方无 error 即成功生成 NotepadReplacer.exe。
pause

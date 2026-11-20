@rem =====================================================================
@rem build_docshot.bat -- compile DocShot driver + FakePlc (README sec.2)
@rem Usage (repo root): tools\DocShot\build_docshot.bat
@rem NOTE: csc needs /codepage:65001 (sources are UTF-8, Chinese strings).
@rem NOTE: keep this .bat ASCII-only -- cmd parses batch in ANSI codepage,
@rem       Chinese text here gets mangled (AGENTS.md rule 6, batch flavor).
@rem =====================================================================
@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REF=C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2

if not exist tools\DocShot\build mkdir tools\DocShot\build

"%CSC%" /nologo /target:exe /codepage:65001 ^
  /out:tools\DocShot\build\DocShot.exe ^
  /r:"%REF%\UIAutomationClient.dll" ^
  /r:"%REF%\UIAutomationTypes.dll" ^
  /r:"%REF%\WindowsBase.dll" ^
  /r:System.Drawing.dll ^
  tools\DocShot\DocShot.cs
if errorlevel 1 ( echo DocShot compile FAILED & exit /b 1 )

"%CSC%" /nologo /target:exe /codepage:65001 ^
  /out:tools\DocShot\build\FakePlc.exe ^
  /r:libs\HslCommunication.dll ^
  tools\DocShot\FakePlc.cs
if errorlevel 1 ( echo FakePlc compile FAILED & exit /b 1 )

copy /Y libs\HslCommunication.dll tools\DocShot\build\ >nul
echo DocShot / FakePlc build OK.
endlocal

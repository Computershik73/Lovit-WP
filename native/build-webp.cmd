@echo off
rem Сборка LovitWebP.dll - разбора картинок WebP.
rem
rem     native\build-webp.cmd            собрать ARM (телефон)
rem     native\build-webp.cmd x86        собрать x86 (эмулятор)
rem
rem Компилятор тот же, что у LovitTls.dll: Visual Studio 2013, потому что
rem Windows Phone 8.1 живёт на среде выполнения msvcr120_app.
rem
rem Из libwebp берётся только разбор: dec, utils и та часть dsp, что нужна
rem для чтения. Кодирование (enc*, cost*, ssim*, lossless_enc*) не нужно
rem вовсе и тянет за собой ещё десяток файлов.

setlocal enabledelayedexpansion

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=arm"

set "HERE=%~dp0"
set "ROOT=%HERE%.."
set "VS12=C:\Program Files (x86)\Microsoft Visual Studio 12.0\VC"
set "WPKIT=C:\Program Files (x86)\Windows Phone Kits\8.1"
set "WEBP=%ROOT%\vendor\libwebp"

if /i "%ARCH%"=="arm" (
  set "HOSTTARGET=x86_arm"
  set "MACHINE=ARM"
  set "CRTDIR=%VS12%\lib\store\arm"
  set "KITLIB=%WPKIT%\lib\ARM"
) else (
  set "HOSTTARGET=x86"
  set "MACHINE=X86"
  set "CRTDIR=%VS12%\lib\store"
  set "KITLIB=%WPKIT%\lib\x86"
)

if not exist "%WEBP%\src\dec\webp_dec.c" (
  echo NET libwebp: ozhidalsya %WEBP%\src
  exit /b 2
)

call "%VS12%\vcvarsall.bat" %HOSTTARGET% > nul
if errorlevel 1 (
  echo ne nastroilos okruzhenie kompilyatora
  exit /b 1
)

set "INCLUDE=%VS12%\include;%WPKIT%\Include;%WPKIT%\Include\minwin;%WPKIT%\Include\mincore;%WPKIT%\Include\abi;%WPKIT%\Include\wrl"
set "LIB=%CRTDIR%;%KITLIB%"

set "OUT=%ROOT%\native\build\webp-%ARCH%"
if not exist "%OUT%" mkdir "%OUT%"

pushd "%OUT%"

rem Список файлов собирается тут же: перечислять сотню имён руками - верный
rem способ забыть один и получить неразрешённый символ на сборке.
set "SOURCES=%ROOT%\native\LovitWebP\lovit_webp.c"

for %%f in ("%WEBP%\src\dec\*.c") do set "SOURCES=!SOURCES! %%f"
for %%f in ("%WEBP%\src\utils\*.c") do set "SOURCES=!SOURCES! %%f"

for %%f in ("%WEBP%\src\dsp\*.c") do (
  set "NAME=%%~nf"
  set "SKIP="
  if "!NAME:~0,3!"=="enc" set "SKIP=1"
  if "!NAME:~0,4!"=="cost" set "SKIP=1"
  if "!NAME:~0,4!"=="ssim" set "SKIP=1"
  if "!NAME:~0,12!"=="lossless_enc" set "SKIP=1"
  if not defined SKIP set "SOURCES=!SOURCES! %%f"
)

rem WEBP_USE_THREAD выключен намеренно: разбор идёт в рабочем потоке
rem приложения, а свои потоки в песочнице телефона заводить незачем.
cl.exe /c /nologo /W1 /O2 /MD /GS ^
  /D_WIN32_WINNT=0x0603 /DWINAPI_FAMILY=WINAPI_FAMILY_PHONE_APP ^
  /D_CRT_SECURE_NO_WARNINGS /DWEBP_DISABLE_STATS /DWEBP_REDUCE_CSP ^
  /I"%WEBP%\src" /I"%WEBP%" /I"%ROOT%\native\LovitWebP" ^
  !SOURCES!
if errorlevel 1 (popd & exit /b 1)

link.exe /nologo /DLL /APPCONTAINER /MACHINE:%MACHINE% /SUBSYSTEM:WINDOWS ^
  /DYNAMICBASE /NXCOMPAT /OPT:REF /OPT:ICF ^
  /NODEFAULTLIB:kernel32.lib /NODEFAULTLIB:ole32.lib ^
  /OUT:LovitWebP.dll ^
  *.obj WindowsPhoneCore.lib RuntimeObject.lib PhoneAppModelHost.lib
if errorlevel 1 (popd & exit /b 1)

popd

set "STAGE=%ROOT%\Lovit\Native\%ARCH%"
if not exist "%STAGE%" mkdir "%STAGE%"
copy /y "%OUT%\LovitWebP.dll" "%STAGE%\LovitWebP.dll" > nul

echo LovitWebP.dll (%ARCH%) gotova: %STAGE%\LovitWebP.dll
endlocal

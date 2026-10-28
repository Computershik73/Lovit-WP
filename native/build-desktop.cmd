@echo off
rem Sborka rodnyh bibliotek dlya nastolnoy Windows (UWP x86).
rem
rem     native\build-desktop.cmd            vse chetyre
rem     native\build-desktop.cmd tls        tolko odnu
rem
rem Otlichiya ot telefonnyh skriptov ryadom - v instrumentah, a ne v kode:
rem
rem   * Visual Studio 2022 vmesto 2013. Telefonu nuzhna byla sreda
rem     msvcr120_app, nastolnym prilozheniyam Windows 10 - vcruntime140_app,
rem     i sobirat ih odnim kompilyatorom nelzya.
rem   * Nabor Windows 10 vmesto Windows Phone Kits 8.1.
rem   * WINAPI_FAMILY_APP vmesto WINAPI_FAMILY_PHONE_APP: to zhe otsechenie
rem     nastolnogo API, no po pravilam desyatki.
rem   * WindowsApp.lib vmesto trekh telefonnyh bibliotek - v desyatke ves
rem     dozvolennyy nabor sobran v odnu.
rem
rem OpenSSL beretsya iz vcpkg (triplet x86-uwp): svoy, a ne sistemnyy - tak
rem zhe, kak na telefone. Postavit odnazhdy:
rem
rem     C:\vcpkg\vcpkg.exe install openssl:x86-uwp

setlocal enabledelayedexpansion

set "WHAT=%~1"
if "%WHAT%"=="" set "WHAT=all"

set "HERE=%~dp0"
set "ROOT=%HERE%.."
rem Triplet static-md: sam OpenSSL vshit v nashu biblioteku, a sreda
rem vypolneniya ostaetsya obshchey - tak zhe, kak sobrano dlya telefona.
set "VCPKG=C:\vcpkg\installed\x86-uwp-static-md"
set "OUTROOT=%ROOT%\native\build"
set "STAGE=%ROOT%\Lovit\Native\x86-uwp"

set "VSWHERE=C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo NET vswhere - ne nayti Visual Studio
  exit /b 2
)

for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -property installationPath`) do set "VS=%%i"
if not exist "%VS%\VC\Auxiliary\Build\vcvarsall.bat" (
  echo NET Visual Studio 2022 s instrumentami C++
  exit /b 2
)

rem uwp v konce - eto i est nabor bibliotek dlya prilozheniy iz magazina:
rem bez nego linkovka voz'met nastolnyy CRT, i paket ne proydet proverku.
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" x86 uwp > nul
if errorlevel 1 (
  echo ne nastroilos okruzhenie kompilyatora
  exit /b 1
)

if not exist "%STAGE%" mkdir "%STAGE%"

set "COMMON=/c /nologo /O2 /MD /GS /D_WIN32_WINNT=0x0A00 /DWINAPI_FAMILY=WINAPI_FAMILY_APP /D_CRT_SECURE_NO_WARNINGS"
set "LINKCOMMON=/nologo /DLL /APPCONTAINER /MACHINE:X86 /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /OPT:REF /OPT:ICF"

if /i "%WHAT%"=="all" goto :tls
if /i "%WHAT%"=="tls" goto :tls
if /i "%WHAT%"=="webp" goto :webp
if /i "%WHAT%"=="opus" goto :opus
if /i "%WHAT%"=="lottie" goto :lottie
echo neizvestno chto sobirat: %WHAT%
exit /b 2

rem ------------------------------------------------------------------ TLS
:tls
if not exist "%VCPKG%\lib\libssl.lib" (
  echo NET OpenSSL dlya x86-uwp: ozhidalsya %VCPKG%\lib\libssl.lib
  echo postavte:  C:\vcpkg\vcpkg.exe install openssl:x86-uwp
  exit /b 2
)

set "OUT=%OUTROOT%\tls-x86-uwp"
if not exist "%OUT%" mkdir "%OUT%"
pushd "%OUT%"
del /q *.obj 2> nul

cl.exe %COMMON% /W3 /DUNICODE /D_UNICODE ^
  /I"%VCPKG%\include" /I"%ROOT%\native\LovitTls" ^
  "%ROOT%\native\LovitTls\lovit_tls.c"
if errorlevel 1 (popd & exit /b 1)

link.exe %LINKCOMMON% /OUT:LovitTls.dll ^
  /LIBPATH:"%VCPKG%\lib" ^
  lovit_tls.obj libssl.lib libcrypto.lib crypt32.lib bcrypt.lib WindowsApp.lib
if errorlevel 1 (popd & exit /b 1)

popd
copy /y "%OUT%\LovitTls.dll" "%STAGE%\LovitTls.dll" > nul
echo LovitTls.dll gotova

if /i not "%WHAT%"=="all" goto :done

rem ----------------------------------------------------------------- WebP
:webp
set "WEBP=%ROOT%\vendor\libwebp"
if not exist "%WEBP%\src\dec\webp_dec.c" (
  echo NET libwebp: ozhidalsya %WEBP%\src
  exit /b 2
)

set "OUT=%OUTROOT%\webp-x86-uwp"
if not exist "%OUT%" mkdir "%OUT%"
pushd "%OUT%"
del /q *.obj 2> nul

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

cl.exe %COMMON% /W1 /DWEBP_DISABLE_STATS /DWEBP_REDUCE_CSP ^
  /I"%WEBP%\src" /I"%WEBP%" /I"%ROOT%\native\LovitWebP" ^
  !SOURCES!
if errorlevel 1 (popd & exit /b 1)

link.exe %LINKCOMMON% /OUT:LovitWebP.dll *.obj WindowsApp.lib
if errorlevel 1 (popd & exit /b 1)

popd
copy /y "%OUT%\LovitWebP.dll" "%STAGE%\LovitWebP.dll" > nul
echo LovitWebP.dll gotova

if /i not "%WHAT%"=="all" goto :done

rem ----------------------------------------------------------------- Opus
:opus
set "OPUS=%ROOT%\vendor\libopus"
if not exist "%OPUS%\src\opus_decoder.c" (
  echo NET libopus: ozhidalsya %OPUS%\src
  exit /b 2
)

if not exist "%OPUS%\win32\version.h" (
  echo #define PACKAGE_VERSION "1.4"> "%OPUS%\win32\version.h"
)

set "OUT=%OUTROOT%\opus-x86-uwp"
if not exist "%OUT%" mkdir "%OUT%"
pushd "%OUT%"
del /q *.obj 2> nul
if exist sources.rsp del sources.rsp

echo "%ROOT%\native\LovitOpus\lovit_opus.c">> sources.rsp

for %%f in ("%OPUS%\celt\*.c") do (
  set "NAME=%%~nf"
  set "SKIP="
  if "!NAME!"=="opus_custom_demo" set "SKIP=1"
  if not defined SKIP echo "%%f">> sources.rsp
)

for %%f in ("%OPUS%\silk\*.c") do echo "%%f">> sources.rsp
for %%f in ("%OPUS%\silk\float\*.c") do echo "%%f">> sources.rsp
for %%f in ("%OPUS%\celt\x86\*.c") do echo "%%f">> sources.rsp
for %%f in ("%OPUS%\silk\x86\*.c") do echo "%%f">> sources.rsp

for %%f in ("%OPUS%\src\*.c") do (
  set "NAME=%%~nf"
  set "SKIP="
  if "!NAME!"=="opus_demo" set "SKIP=1"
  if "!NAME!"=="opus_compare" set "SKIP=1"
  if "!NAME!"=="repacketizer_demo" set "SKIP=1"
  if not defined SKIP echo "%%f">> sources.rsp
)

cl.exe %COMMON% /W1 /DHAVE_CONFIG_H ^
  /I"%OPUS%\win32" /I"%OPUS%\include" /I"%OPUS%\celt" /I"%OPUS%\silk" ^
  /I"%OPUS%\silk\float" /I"%OPUS%" /I"%ROOT%\native\LovitOpus" ^
  @sources.rsp
if errorlevel 1 (popd & exit /b 1)

link.exe %LINKCOMMON% /OUT:LovitOpus.dll *.obj WindowsApp.lib
if errorlevel 1 (popd & exit /b 1)

popd
copy /y "%OUT%\LovitOpus.dll" "%STAGE%\LovitOpus.dll" > nul
echo LovitOpus.dll gotova

if /i not "%WHAT%"=="all" goto :done

rem --------------------------------------------------------------- Lottie
:lottie
call "%HERE%build-lottie-desktop.cmd"
if errorlevel 1 exit /b 1

:done
echo.
echo gotovo: %STAGE%
endlocal

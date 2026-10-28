@echo off
rem Sborka LovitOpus.dll - dekoder golosovyh dlya "Lovita".
rem
rem     native\build-opus.cmd            sobrat ARM (telefon)
rem     native\build-opus.cmd x86        sobrat x86 (emulyator)
rem
rem Sobiraetsya temi zhe pryamymi vyzovami kompilyatora VS 2013, chto i
rem ostalnye nativnye chasti: tipa proekta "Windows Phone 8.1 (C++)" v etoy
rem ustanovke Visual Studio net, a kompilyator pod ARM i zagolovki
rem telefonnogo nabora - est.

setlocal enabledelayedexpansion

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=arm"

set "HERE=%~dp0"
set "ROOT=%HERE%.."
set "VS12=C:\Program Files (x86)\Microsoft Visual Studio 12.0\VC"
set "WPKIT=C:\Program Files (x86)\Windows Phone Kits\8.1"
set "OPUS=%ROOT%\vendor\libopus"

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

if not exist "%OPUS%\src\opus_decoder.c" (
  echo NET libopus: ozhidalsya %OPUS%\src
  exit /b 2
)

rem config.h iz postavki trebuet version.h, kotoryy obychno porozhdaetsya
rem sborochnoy sistemoy. Delaem ego sami - odnoy strokoy.
if not exist "%OPUS%\win32\version.h" (
  echo #define PACKAGE_VERSION "1.4"> "%OPUS%\win32\version.h"
)

call "%VS12%\vcvarsall.bat" %HOSTTARGET% > nul
if errorlevel 1 (
  echo ne nastroilos okruzhenie kompilyatora
  exit /b 1
)

set "INCLUDE=%VS12%\include;%WPKIT%\Include;%WPKIT%\Include\minwin;%WPKIT%\Include\mincore;%WPKIT%\Include\abi;%WPKIT%\Include\wrl"
set "LIB=%CRTDIR%;%KITLIB%"

set "OUT=%ROOT%\native\build\opus-%ARCH%"
if not exist "%OUT%" mkdir "%OUT%"

pushd "%OUT%"

if exist sources.rsp del sources.rsp
echo "%ROOT%\native\LovitOpus\lovit_opus.c">> sources.rsp

rem celt i silk celikom, krome demo. Optimizacii pod ARM ne berem narochno:
rem tam est assembler i otdelnye pravila sborki, a golos raskodirovyvaetsya i
rem obychnym C bystree realnogo vremeni.
rem
rem A vot pod x86 vzyat ih prihoditsya: config.h dlya MSVC sam vklyuchaet
rem OPUS_X86_MAY_HAVE_SSE, i koder ssylaetsya na SSE-varianty. Bez nih sborka
rem x86 ne slinkuetsya - imenno na kodere, dekoder obhodilsya bez nih.
for %%f in ("%OPUS%\celt\*.c") do (
  set "NAME=%%~nf"
  set "SKIP="
  if "!NAME!"=="opus_custom_demo" set "SKIP=1"
  if not defined SKIP echo "%%f">> sources.rsp
)

for %%f in ("%OPUS%\silk\*.c") do echo "%%f">> sources.rsp

rem Schet s plavayushchey tochkoy - kak v postavlyaemom config.h dlya MSVC.
for %%f in ("%OPUS%\silk\float\*.c") do echo "%%f">> sources.rsp

if /i not "%ARCH%"=="arm" (
  for %%f in ("%OPUS%\celt\x86\*.c") do echo "%%f">> sources.rsp
  for %%f in ("%OPUS%\silk\x86\*.c") do echo "%%f">> sources.rsp
)

for %%f in ("%OPUS%\src\*.c") do (
  set "NAME=%%~nf"
  set "SKIP="
  if "!NAME!"=="opus_demo" set "SKIP=1"
  if "!NAME!"=="opus_compare" set "SKIP=1"
  if "!NAME!"=="repacketizer_demo" set "SKIP=1"
  if not defined SKIP echo "%%f">> sources.rsp
)

rem HAVE_CONFIG_H tyanet win32\config.h - v nem OPUS_BUILD i USE_ALLOCA.
cl.exe /c /nologo /W1 /O2 /MD /GS ^
  /D_WIN32_WINNT=0x0603 /DWINAPI_FAMILY=WINAPI_FAMILY_PHONE_APP ^
  /D_CRT_SECURE_NO_WARNINGS /DHAVE_CONFIG_H ^
  /I"%OPUS%\win32" /I"%OPUS%\include" /I"%OPUS%\celt" /I"%OPUS%\silk" ^
  /I"%OPUS%\silk\float" /I"%OPUS%" /I"%ROOT%\native\LovitOpus" ^
  @sources.rsp
if errorlevel 1 (popd & exit /b 1)

link.exe /nologo /DLL /APPCONTAINER /MACHINE:%MACHINE% /SUBSYSTEM:WINDOWS ^
  /DYNAMICBASE /NXCOMPAT /OPT:REF /OPT:ICF ^
  /NODEFAULTLIB:kernel32.lib /NODEFAULTLIB:ole32.lib ^
  /OUT:LovitOpus.dll ^
  *.obj WindowsPhoneCore.lib RuntimeObject.lib PhoneAppModelHost.lib
if errorlevel 1 (popd & exit /b 1)

popd

set "STAGE=%ROOT%\Lovit\Native\%ARCH%"
if not exist "%STAGE%" mkdir "%STAGE%"
copy /y "%OUT%\LovitOpus.dll" "%STAGE%\LovitOpus.dll" > nul

echo LovitOpus.dll (%ARCH%) gotova: %STAGE%\LovitOpus.dll
endlocal

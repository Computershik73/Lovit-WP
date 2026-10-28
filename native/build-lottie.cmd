@echo off
rem Sborka LovitLottie.dll - otrisovshchik dvizhushchihsya stikerov.
rem
rem     native\build-lottie.cmd            sobrat ARM (telefon)
rem
rem Sobiraetsya VS 2013, kak i ostalnye nativnye chasti: sredy vypolneniya
rem ot VS 2015 pod Windows Phone 8.1 ne sushchestvuet. Ishodniki rlottie
rem pered etim privodyatsya k VS 2013 stsenariem tools\patch-rlottie.sh -
rem bez nego sborka ne poydyot.

setlocal enabledelayedexpansion

set "ARCH=arm"
set "HERE=%~dp0"
set "ROOT=%HERE%.."
set "VS14=C:\Program Files (x86)\Microsoft Visual Studio 12.0\VC"
set "WPKIT=C:\Program Files (x86)\Windows Phone Kits\8.1"
set "LOT=%ROOT%\vendor\rlottie"

if not exist "%LOT%\src\lottie\lottieanimation.cpp" (
  echo NET rlottie
  exit /b 2
)

call "%VS14%\vcvarsall.bat" x86_arm > nul
if errorlevel 1 (
  echo ne nastroilos okruzhenie kompilyatora
  exit /b 1
)

rem Obshchaya biblioteka C u VS 2015 lezhit ne v papke kompilyatora, a v
rem nabore Windows 10 - bez neyo ne nahoditsya dazhe stdio.h. Beryom versiyu
rem 10240: ona rovesnica i VS 2015, i telefonnogo nabora 8.1.
set "UCRT=C:\Program Files (x86)\Windows Kits\10"
set "UCRTVER=10.0.10240.0"

set "INCLUDE=%VS14%\include;%WPKIT%\Include;%WPKIT%\Include\minwin;%WPKIT%\Include\mincore;%WPKIT%\Include\abi;%WPKIT%\Include\wrl"
rem Vshivaemaya sreda vypolneniya lezhit vne papki "dlya magazina" - tam
rem tolko variant s podgruzkoy. Poetomu obychnaya papka arm idyot sledom.
set "LIB=%VS14%\lib\store\arm;%WPKIT%\lib\ARM"

set "OUT=%ROOT%\native\build\lottie-%ARCH%"
if not exist "%OUT%" mkdir "%OUT%"

pushd "%OUT%"

if exist sources.rsp erase /q sources.rsp
echo "%ROOT%\native\LovitLottie\lovit_lottie.cpp">> sources.rsp

for %%f in ("%LOT%\src\lottie\*.cpp") do echo "%%f">> sources.rsp
for %%f in ("%LOT%\src\vector\*.cpp") do echo "%%f">> sources.rsp
for %%f in ("%LOT%\src\vector\freetype\*.cpp") do echo "%%f">> sources.rsp
for %%f in ("%LOT%\src\vector\stb\*.cpp") do echo "%%f">> sources.rsp

rem NEON otklyuchen narochno: tam otdelnyy assembler so svoimi pravilami
rem sborki, a stikery melkie i risuyutsya bystro i obychnym kodom.
cl.exe /c /nologo /W0 /O2 /MD /GS /EHsc /GR- ^
  /D_WIN32_WINNT=0x0603 /DWINAPI_FAMILY=WINAPI_FAMILY_PHONE_APP ^
  /D_CRT_SECURE_NO_WARNINGS /DLOTTIE_DISABLE_ARM_NEON /DNDEBUG /DRLOTTIE_BUILD ^
  /Dsnprintf=_snprintf ^
  /I"%ROOT%\native\LovitLottie" ^
  /I"%LOT%\inc" /I"%LOT%\src\lottie" /I"%LOT%\src\vector" ^
  /I"%LOT%\src\vector\freetype" /I"%LOT%\src\vector\pixman" /I"%LOT%\src\vector\stb" ^
  @sources.rsp
if errorlevel 1 (popd & exit /b 1)

link.exe /nologo /DLL /APPCONTAINER /MACHINE:ARM /SUBSYSTEM:WINDOWS ^
  /DYNAMICBASE /NXCOMPAT /OPT:REF /OPT:ICF ^
  /NODEFAULTLIB:kernel32.lib /NODEFAULTLIB:ole32.lib ^
  /OUT:LovitLottie.dll ^
  *.obj WindowsPhoneCore.lib RuntimeObject.lib PhoneAppModelHost.lib
if errorlevel 1 (popd & exit /b 1)

popd

set "STAGE=%ROOT%\Lovit\Native\%ARCH%"
if not exist "%STAGE%" mkdir "%STAGE%"
copy /y "%OUT%\LovitLottie.dll" "%STAGE%\LovitLottie.dll" > nul

echo LovitLottie.dll gotova: %STAGE%\LovitLottie.dll
endlocal

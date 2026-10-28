@echo off
rem Сборка LovitTls.dll - своей криптографии для <Ловита>.
rem
rem     native\build.cmd            собрать ARM (телефон)
rem     native\build.cmd x86        собрать x86 (эмулятор, нужен свой OpenSSL)
rem
rem Собирается не через MSBuild, а прямыми вызовами компилятора: типа
rem проекта <Windows Phone 8.1 (C++)> в этой установке Visual Studio нет,
rem а компилятор под ARM и заголовки телефонного набора - есть. Проще позвать
rem их напрямую, чем чинить систему проектов.
rem
rem Компилятор берётся из Visual Studio 2013: Windows Phone 8.1 живёт на
rem среде выполнения msvcr120_app, и собранное VS 2015 на телефоне просто
rem не с чем запустить.
rem
rem Кавычки вокруг всех присваиваний не для красоты: в путях есть <(x86)>,
rem и незакавыченная скобка закрывает блок if раньше времени.

setlocal

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=arm"

set "HERE=%~dp0"
set "ROOT=%HERE%.."
set "VS12=C:\Program Files (x86)\Microsoft Visual Studio 12.0\VC"
set "WPKIT=C:\Program Files (x86)\Windows Phone Kits\8.1"

if /i "%ARCH%"=="arm" (
  set "HOSTTARGET=x86_arm"
  set "MACHINE=ARM"
  set "CRTDIR=%VS12%\lib\store\arm"
  set "KITLIB=%WPKIT%\lib\ARM"
  set "SSLDIR=%ROOT%\vendor\openssl-wp81-arm"
) else (
  set "HOSTTARGET=x86"
  set "MACHINE=X86"
  set "CRTDIR=%VS12%\lib\store"
  set "KITLIB=%WPKIT%\lib\x86"
  set "SSLDIR=%ROOT%\vendor\openssl-wp81-x86"
)

if not exist "%SSLDIR%\lib\ssleay32.lib" (
  echo NET OpenSSL dlya %ARCH%: ozhidalsya %SSLDIR%\lib\ssleay32.lib
  exit /b 2
)

if not exist "%VS12%\vcvarsall.bat" (
  echo NET Visual Studio 2013 - ona nuzhna radi sredy vypolneniya telefona
  exit /b 2
)

call "%VS12%\vcvarsall.bat" %HOSTTARGET% > nul
if errorlevel 1 (
  echo ne nastroilos okruzhenie kompilyatora
  exit /b 1
)

rem Заголовки телефонного набора разложены по нескольким папкам, и одна
rem ссылается на другую: без minwin уже windef.h не находит minwindef.h.
set "INCLUDE=%VS12%\include;%WPKIT%\Include;%WPKIT%\Include\minwin;%WPKIT%\Include\mincore;%WPKIT%\Include\abi;%WPKIT%\Include\wrl"
set "LIB=%CRTDIR%;%KITLIB%;%SSLDIR%\lib"

set "OUT=%ROOT%\native\build\%ARCH%"
if not exist "%OUT%" mkdir "%OUT%"

pushd "%OUT%"

rem WINAPI_FAMILY_PHONE_APP отсекает всё, чего в песочнице телефона нет:
rem если код случайно позовёт настольную часть Windows, это выяснится
rem сейчас, а не при первом запуске на устройстве.
cl.exe /c /nologo /W3 /O2 /MD /GS ^
  /D_WIN32_WINNT=0x0603 /DWINAPI_FAMILY=WINAPI_FAMILY_PHONE_APP ^
  /D_CRT_SECURE_NO_WARNINGS /DUNICODE /D_UNICODE ^
  /I"%SSLDIR%\include" /I"%ROOT%\native\LovitTls" ^
  "%ROOT%\native\LovitTls\lovit_tls.c"
if errorlevel 1 (popd & exit /b 1)

rem kernel32 и ole32 в телефонном контейнере не существуют: то же самое
rem отдаёт WindowsPhoneCore.lib, и линковщику надо сказать не искать их.
link.exe /nologo /DLL /APPCONTAINER /MACHINE:%MACHINE% /SUBSYSTEM:WINDOWS ^
  /DYNAMICBASE /NXCOMPAT /OPT:REF /OPT:ICF ^
  /NODEFAULTLIB:kernel32.lib /NODEFAULTLIB:ole32.lib ^
  /OUT:LovitTls.dll ^
  lovit_tls.obj ssleay32.lib libeay32.lib ws2_32.lib ^
  WindowsPhoneCore.lib RuntimeObject.lib PhoneAppModelHost.lib
if errorlevel 1 (popd & exit /b 1)

popd

rem Библиотека кладётся туда, откуда её забирает пакет приложения.
set "STAGE=%ROOT%\Lovit\Native\%ARCH%"
if not exist "%STAGE%" mkdir "%STAGE%"
copy /y "%OUT%\LovitTls.dll" "%STAGE%\LovitTls.dll" > nul

echo LovitTls.dll (%ARCH%) gotova: %STAGE%\LovitTls.dll
endlocal

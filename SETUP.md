# Сборка Ловита для Windows Phone 8.1

Короче, чем кажется: родные библиотеки (`LovitTls`, `LovitOpus`,
`LovitWebP`, `LovitLottie`) лежат собранными в `Lovit\Native\`, и для самого
приложения нужна одна Visual Studio 2015. Пересобирать их понадобится,
только если правите код в `native\`.

## Что нужно

* Windows 10 или 11, x64;
* **Visual Studio 2015** с компонентом «Средства разработки для Windows
  Phone 8.1» и **Windows Phone SDK 8.1** — в новых Visual Studio такого типа
  проекта нет;
* для установки на телефон — телефон, разблокированный для разработчика
  (Windows Phone Developer Registration из того же SDK).

## 1. Ключ для подписи

Закрытого ключа в репозитории нет, его делают один раз. В PowerShell из
корня репозитория:

```bash
powershell -Command "$c = New-SelfSignedCertificate -Type Custom -Subject 'CN=Computershik' -KeyUsage DigitalSignature -KeyExportPolicy Exportable -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3'); [IO.File]::WriteAllBytes('Lovit\Lovit_TemporaryKey.pfx', $c.Export('Pfx')); $c.Thumbprint"
```

Команда напечатает отпечаток. Его надо вписать в `PackageCertificateThumbprint`
в `Lovit\Lovit.csproj` и `Lovit.Desktop\Lovit.Desktop.csproj` — без него
подпись молча не делается.

`Subject` сертификата обязан совпадать с `Publisher` в
`Lovit\Package.appxmanifest`. Хотите своё имя — меняйте оба. Но пакет с
другим издателем телефон считает другим приложением: поверх уже
установленного Ловита он не встанет, а данные останутся у старого.

## 2. Сборка

Открыть `Lovit.sln` и собрать, либо из командной строки:

```bash
"C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe" Lovit.sln /p:Configuration=Release /p:Platform=ARM
```

Пакет появится в `Lovit\AppPackages\`, рядом — `.cer` с сертификатом.

* `ARM` — для телефона, `x86` — для эмулятора (там нет своего TLS,
  приложение ходит системным).
* `Release` собирается .NET Native и идёт заметно дольше `Debug`.
* Последнее число версии в `Package.appxmanifest` растёт на каждой сборке
  само (шаг `BumpPackageRevision`): без этого телефон не ставит пакет
  поверх. Так что манифест после сборки изменён — так и задумано.

## 3. Установка

```bash
"C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\AppDeploy\AppDeployCmd.exe" /installlaunch "Lovit\AppPackages\Lovit_1.1.0.N_ARM_Test\Lovit_1.1.0.N_ARM.appx" /targetdevice:de
```

`N` — номер вашей сборки. `de` — телефон по USB, `xd` — эмулятор.

## Журнал

В `Debug` журнал пишет всё — с перепиской, именами и адресами вложений. В
`Release` — только падения. **Отладочную сборку людям не раздавайте.**
Смотреть журнал — в приложении: настройки → «журнал».

## Родные библиотеки

Нужны только при правке `native\`. Компилятор — из **Visual Studio 2013**:
Windows Phone 8.1 живёт на среде `msvcr120_app`, а собранное VS 2015 на
телефоне не запустится. Проекты не нужны — скрипты зовут `cl.exe` и
`link.exe` напрямую.

```bash
native\build.cmd
native\build-opus.cmd
native\build-webp.cmd
native\build-lottie.cmd
```

`build-opus.cmd` и `build-webp.cmd` принимают `x86` — для эмулятора.
`build-lottie.cmd` ждёт исходники rlottie, приведённые к VS 2013, — это
делает `native\tools\patch-rlottie.sh` (в `vendor\rlottie` они уже
приведены). Готовые DLL кладутся в `Lovit\Native\<арх>\`.

OpenSSL 1.0.2l под ARM лежит собранным в `vendor\openssl-wp81-arm`.

## Lovit.Desktop

Та же программа для Windows 10/11, из тех же исходников. Нужна Visual
Studio 2022 с компонентом UWP; сборка и установка — в
[Lovit.Desktop/README.md](Lovit.Desktop/README.md). Её родные библиотеки
пересобирает `native\build-desktop.cmd` (Visual Studio 2022 и OpenSSL из
vcpkg: `vcpkg install openssl:x86-uwp`).

## Чего в репозитории нет

* `Lovit_TemporaryKey.pfx` — закрытый ключ подписи (см. шаг 1);
* `bin\`, `obj\`, `AppPackages\`, `native\build\` — всё, что делает сборка;
* сводки к сборкам и журналы с телефонов — в них куски переписки.

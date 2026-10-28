# Собирает roots.h из сертификатов в LovitTls\certs.
#
# Корни вшиты в саму библиотеку, а не лежат файлами в пакете: файл можно
# подменить, а зашитый в код — уже нет, и вся затея с проверкой цепочки
# держится именно на этом.
#
#     powershell -ExecutionPolicy Bypass -File native\tools\make-roots.ps1

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$certs = Join-Path (Split-Path -Parent $here) "LovitTls\certs"
$target = Join-Path (Split-Path -Parent $here) "LovitTls\roots.h"

$text = New-Object Text.StringBuilder
[void]$text.AppendLine("/* Порождено native\tools\make-roots.ps1 — правки затрутся. */")
[void]$text.AppendLine("")
[void]$text.AppendLine("#ifndef LOVIT_ROOTS_H")
[void]$text.AppendLine("#define LOVIT_ROOTS_H")
[void]$text.AppendLine("")

$names = @()

foreach ($file in Get-ChildItem $certs -Filter *.der | Sort-Object Name) {
    $name = "lovit_root_" + ($file.BaseName -replace '[^0-9A-Za-z]', '_')
    $names += $name

    $bytes = [IO.File]::ReadAllBytes($file.FullName)

    [void]$text.AppendLine("/* $($file.Name), $($bytes.Length) байт */")
    [void]$text.AppendLine("static const unsigned char $name[] = {")

    for ($offset = 0; $offset -lt $bytes.Length; $offset += 12) {
        $slice = $bytes[$offset..([Math]::Min($offset + 11, $bytes.Length - 1))]
        $line = ($slice | ForEach-Object { "0x{0:x2}" -f $_ }) -join ", "
        [void]$text.AppendLine("    $line,")
    }

    [void]$text.AppendLine("};")
    [void]$text.AppendLine("")
}

[void]$text.AppendLine("typedef struct {")
[void]$text.AppendLine("    const unsigned char *data;")
[void]$text.AppendLine("    int length;")
[void]$text.AppendLine("} lovit_root;")
[void]$text.AppendLine("")
[void]$text.AppendLine("static const lovit_root lovit_roots[] = {")

foreach ($name in $names) {
    [void]$text.AppendLine("    { $name, (int)sizeof($name) },")
}

[void]$text.AppendLine("};")
[void]$text.AppendLine("")
[void]$text.AppendLine("#define LOVIT_ROOTS_COUNT ((int)(sizeof(lovit_roots) / sizeof(lovit_roots[0])))")
[void]$text.AppendLine("")
[void]$text.AppendLine("#endif")

# С меткой порядка байтов: без неё компилятор Visual Studio читает файл
# в кодировке системы и спотыкается о русские буквы в примечаниях.
[IO.File]::WriteAllText($target, $text.ToString(), (New-Object Text.UTF8Encoding $true))

Write-Output "roots.h: $($names.Count) корней"

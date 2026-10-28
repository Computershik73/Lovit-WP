#!/usr/bin/env python3
"""Проверка SESSION_INIT: принимает ли сервер версию клиента.

Собирает то же приветствие, что и приложение, и печатает ответ сервера:

    python3 tools/session-check.py [версия] [номер сборки]

Признак `app-update-type` в ответе означает «приложение устарело» — ровно
этими словами сервер потом откажет при входе. У принятой версии ответ
заметно длиннее и никакого признака в нём нет.
"""
import os
import socket
import ssl
import struct
import sys
import tempfile
import time
import uuid

HOST = "api2.oneme.ru"
PORT = 443

APP_VERSION = sys.argv[1] if len(sys.argv) > 1 else "26.23.2"
BUILD_NUMBER = int(sys.argv[2]) if len(sys.argv) > 2 else 6779


SIGNATURE_DIGEST = "1684414033eb263e2c615f8b7df5ed8793850a07656304997fbf07e9e21e1e93"
DEX_DIGEST = "38cff46f392dc1734c308be011c2f0d8da152a390b41063dbb2c913e3032f4b3"
SO_DIGEST = "634ecc42b246784d975f180b4fecf903df235cdf0476da47163a85630eb1a6a8"


def fingerprint(calls_seed, device_id):
    """Отпечаток сборки — то же, что считает ChatCacheFingerprint.cs."""
    import hashlib

    seed = (calls_seed & 0xFFFFFFFFFFFFFFFF).to_bytes(8, "big")
    device = device_id.encode("utf-8")

    out = b""
    for digest in (SIGNATURE_DIGEST, DEX_DIGEST, SO_DIGEST):
        out += hashlib.sha256(bytes.fromhex(digest) + seed + device).digest()
    return out


def roots_file():
    """Корни НУЦ Минцифры из тех же файлов, что вшиты в LovitTls."""
    here = os.path.dirname(os.path.abspath(__file__))
    certs = os.path.join(os.path.dirname(here), "native", "LovitTls", "certs")

    combined = os.path.join(tempfile.gettempdir(), "lovit-roots.pem")

    with open(combined, "w") as target:
        for name in ("russian_trusted_root_ca.der", "russian_trusted_sub_ca.der"):
            with open(os.path.join(certs, name), "rb") as source:
                target.write(ssl.DER_cert_to_PEM_cert(source.read()))

    return combined


def pack(value):
    if isinstance(value, str):
        raw = value.encode("utf-8")
        if len(raw) < 32:
            return bytes([0xA0 | len(raw)]) + raw
        return b"\xd9" + bytes([len(raw)]) + raw
    if isinstance(value, bool):
        return b"\xc3" if value else b"\xc2"
    if isinstance(value, int):
        # Как в приложении: самое короткое представление. Сервер разбирает
        # buildNumber как int, и uint64 на его месте он отвергает.
        if -32 <= value < 0:
            return bytes([0xE0 | (value + 32)])
        if 0 <= value < 128:
            return bytes([value])
        if value < 0x100:
            return b"\xcc" + bytes([value])
        if value < 0x10000:
            return b"\xcd" + struct.pack(">H", value)
        if value < 0x100000000:
            return b"\xce" + struct.pack(">I", value)
        return b"\xcf" + struct.pack(">Q", value)
    if isinstance(value, (bytes, bytearray)):
        return b"\xc4" + bytes([len(value)]) + bytes(value)
    if isinstance(value, dict):
        out = bytes([0x80 | len(value)]) if len(value) < 16 else b"\xde" + struct.pack(">H", len(value))
        for key, item in value.items():
            out += pack(key) + pack(item)
        return out
    raise TypeError(type(value))


def lz4_block(data, expected):
    """Распаковка блока LZ4 — тем же способом, что Lz4.cs в приложении."""
    out = bytearray()
    offset = 0

    while offset < len(data):
        token = data[offset]
        offset += 1

        literals = token >> 4
        if literals == 15:
            while True:
                more = data[offset]
                offset += 1
                literals += more
                if more != 255:
                    break

        out += data[offset:offset + literals]
        offset += literals

        if offset >= len(data):
            break

        distance = data[offset] | (data[offset + 1] << 8)
        offset += 2

        length = (token & 0x0F) + 4
        if (token & 0x0F) == 15:
            while True:
                more = data[offset]
                offset += 1
                length += more
                if more != 255:
                    break

        start = len(out) - distance
        for i in range(length):
            out.append(out[start + i])

    return bytes(out[:expected]) if expected else bytes(out)


def unpack(data, offset):
    """Ровно столько MessagePack, сколько нужно прочитать ответ сервера."""
    marker = data[offset]
    offset += 1

    if marker <= 0x7F:
        return marker, offset
    if marker >= 0xE0:
        return marker - 256, offset
    if 0x80 <= marker <= 0x8F:
        return read_map(data, offset, marker & 0x0F)
    if 0x90 <= marker <= 0x9F:
        return read_array(data, offset, marker & 0x0F)
    if 0xA0 <= marker <= 0xBF:
        size = marker & 0x1F
        return data[offset:offset + size].decode("utf-8", "replace"), offset + size

    if marker == 0xC0:
        return None, offset
    if marker == 0xC2:
        return False, offset
    if marker == 0xC3:
        return True, offset
    if marker in (0xCC, 0xCD, 0xCE, 0xCF):
        size = 1 << (marker - 0xCC)
        return int.from_bytes(data[offset:offset + size], "big"), offset + size
    if marker in (0xD0, 0xD1, 0xD2, 0xD3):
        size = 1 << (marker - 0xD0)
        return int.from_bytes(data[offset:offset + size], "big", signed=True), offset + size
    if marker in (0xD9, 0xDA, 0xDB):
        width = 1 << (marker - 0xD9)
        size = int.from_bytes(data[offset:offset + width], "big")
        offset += width
        return data[offset:offset + size].decode("utf-8", "replace"), offset + size
    if marker in (0xDC, 0xDD):
        width = 2 if marker == 0xDC else 4
        size = int.from_bytes(data[offset:offset + width], "big")
        return read_array(data, offset + width, size)
    if marker in (0xDE, 0xDF):
        width = 2 if marker == 0xDE else 4
        size = int.from_bytes(data[offset:offset + width], "big")
        return read_map(data, offset + width, size)

    raise ValueError("неизвестная метка 0x%02x на %d" % (marker, offset - 1))


def read_map(data, offset, count):
    result = {}
    for _ in range(count):
        key, offset = unpack(data, offset)
        value, offset = unpack(data, offset)
        result[key] = value
    return result, offset


def read_array(data, offset, count):
    result = []
    for _ in range(count):
        item, offset = unpack(data, offset)
        result.append(item)
    return result, offset


def frame(opcode, payload, seq):
    body = pack(payload)
    header = bytes([10, 0]) + struct.pack(">HH", seq, opcode) + bytes([0]) + \
        len(body).to_bytes(3, "big")
    return header + body


def exchange(tls, opcode, payload, seq):
    """Отправляет запрос и возвращает разобранный ответ."""
    tls.sendall(frame(opcode, payload, seq))

    header = tls.recv(10)
    if len(header) < 10:
        raise SystemExit("короткий ответ: %r" % header)

    length = int.from_bytes(header[7:10], "big")

    body = b""
    while len(body) < length:
        chunk = tls.recv(length - len(body))
        if not chunk:
            break
        body += chunk

    if header[6] != 0:
        body = lz4_block(body, length * header[6])

    decoded, _ = unpack(body, 0) if body else (None, 0)
    return header[1], decoded


def show(title, cmd, decoded):
    print("%s: cmd %d (1 = принято, 3 = отказ)" % (title, cmd))
    if not isinstance(decoded, dict):
        print("   ", decoded)
        return

    for key in sorted(decoded):
        value = decoded[key]
        if isinstance(value, list) and len(value) > 6:
            value = value[:6] + ["…(%d)" % len(value)]
        print("   %-28s %s" % (key, value))


DEVICE_ID = uuid.uuid4().hex

user_agent = {
    "deviceType": "ANDROID",
    "appVersion": APP_VERSION,
    "osVersion": "Android 14",
    "timezone": "Europe/Moscow",
    "screen": "420dpi 420dpi 1440x3200",
    "pushDeviceType": "GCM",
    "arch": "arm64-v8a",
    "locale": "ru",
    "buildNumber": BUILD_NUMBER,
    "deviceName": "Xiaomi 14 Ultra",
    "deviceLocale": "ru",
}

payload = {
    "mt_instanceid": uuid.uuid4().hex,
    "userAgent": user_agent,
    "clientSessionId": int(time.time() * 1000),
    "deviceId": DEVICE_ID,
}

context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
context.load_verify_locations(roots_file())

with socket.create_connection((HOST, PORT), timeout=15) as raw:
    with context.wrap_socket(raw, server_hostname=HOST) as tls:
        print("TLS:", tls.version(), tls.cipher()[0])

        cmd, decoded = exchange(tls, 6, payload, 1)
        show("SESSION_INIT", cmd, decoded)

        # Проверка формы запроса LOGIN — с заведомо негодным токеном.
        # Настоящего входа не происходит и SMS никому не уходит: смотрим лишь
        # на то, ругается сервер на разбор запроса или уже на сам токен.
        calls_seed = decoded.get("callsSeed") if isinstance(decoded, dict) else None
        if calls_seed is None:
            print("\ncallsSeed не пришёл — отпечаток сборки не посчитать")
            raise SystemExit(0)

        mark = fingerprint(calls_seed, DEVICE_ID)
        print("\nотпечаток сборки: %d байт, начало %s…" % (len(mark), mark[:8].hex()))

        login = {
            "token": "заведомо негодный",
            "interactive": True,
            "exp": {"chatsCountGroups": b"\x0b\x32"},
            "presenceSync": -1,
            "chatsSync": -1,
            "chatCacheFingerprint": mark,
        }

        cmd, decoded = exchange(tls, 19, login, 2)
        show("\nLOGIN", cmd, decoded)

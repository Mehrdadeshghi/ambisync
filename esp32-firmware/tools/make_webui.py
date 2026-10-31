#!/usr/bin/env python3
"""Erzeugt AmbiSync/webui.h aus webui-src/index.html (gzip-komprimiert als Byte-Liste).

Warum keine Text-Konstante: Die Arduino IDE sucht im Sketch nach Funktionen und fügt
Prototypen ein. JavaScript-Zeilen wie "async function refresh(){" hält sie für C++ und
schreibt dann Code mitten in die Webseite. Eine Byte-Liste kann sie nicht missverstehen.
Aufruf:  python3 tools/make_webui.py
"""
import gzip, os
here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(here, '..', 'webui-src', 'index.html')
dst = os.path.join(here, '..', 'AmbiSync', 'webui.h')
raw = open(src, 'rb').read()
gz = gzip.compress(raw, compresslevel=9, mtime=0)
lines = []
for i in range(0, len(gz), 20):
    lines.append('  ' + ','.join('0x%02x' % b for b in gz[i:i + 20]) + ',')
out = ('#pragma once\n'
       '// Weboberfläche von AmbiSync - NICHT von Hand ändern.\n'
       '// Quelle: esp32-firmware/webui-src/index.html, erzeugt mit tools/make_webui.py\n'
       '// (%d Bytes HTML, gzip-komprimiert %d Bytes)\n'
       'static const size_t INDEX_HTML_GZ_LEN = %d;\n'
       'static const uint8_t INDEX_HTML_GZ[] PROGMEM = {\n%s\n};\n') % (len(raw), len(gz), len(gz), '\n'.join(lines))
open(dst, 'w', encoding='utf-8').write(out)
print('webui.h: %d -> %d Bytes' % (len(raw), len(gz)))

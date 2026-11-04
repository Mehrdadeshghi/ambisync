# AmbiSync – eigenes „Hue Sync“ für den Samsung-Fernseher

Die LEDs hinter dem Fernseher folgen dem Bild, und zwar **auch im Menü und auf der Startseite**. Dafür braucht es keine Kamera, keine Hue Bridge und keinen Raspberry Pi, nur den Fernseher und einen ESP32 mit LED-Streifen.

```
Samsung-TV (Tizen 9)                                   ESP32 + LED-Streifen
┌──────────────────────────────┐   WLAN, UDP            ┌──────────────────────────┐
│ AmbiSync-App (Hintergrund)   │ ──Farben 25×/s────────▶│ AmbiSync-Firmware        │
│  • Video-Ebene  (Filme, TV)  │ ──Status alle 2 s─────▶│  • weiche Übergänge 60/s │
│  • UI-Ebene     (Menüs)      │ ◀─Einstellungen───────│  • Strombegrenzung       │
│  • Samsung-Farbmessung (DRM) │                        │  • Weboberfläche         │
└──────────────────────────────┘                        └──────────────────────────┘
```

## Was ist was

| Ordner | Inhalt |
|---|---|
| `tv-app/` | App für den Fernseher (Tizen .NET). Basiert auf [TizenAmbilight](https://github.com/RitwickVerma/TizenAmbilight) (MIT) und ist um die Menü-Erkennung, die automatische ESP32-Suche und das Statuspaket erweitert. Die Bild-Dump-Funktion des Originals ist entfernt. |
| `esp32-firmware/` | Firmware für den ESP32 (Arduino-Core 3.x, ohne weitere Bibliotheken) |
| `.github/workflows/` | baut beides automatisch auf GitHub |

## Stand

| Teil | geprüft | offen |
|---|---|---|
| Menü-Logik | 17 Tests mit synthetischen Bildern (transparente und deckende Menüs, Seitenleisten, Startseite, eingefrorene Oberfläche) | wie die UI-Ebene auf **deinem** Q7F wirklich aussieht |
| TV ↔ ESP32 | Suche, Statuspaket und Farbpakete gegen einen nachgebauten ESP32 getestet | – |
| ESP32-Firmware | kompiliert für ESP32, ESP32-S3 und ESP32-C3 | Test an echter Hardware |
| Auslesen auf dem Q7F | – | braucht laut einem Nutzerbericht ein **Partner-Zertifikat** |

Die Weboberfläche des ESP32 zeigt unter „Fernseher-App“, was auf dem Fernseher passiert. Darüber lässt sich der erste Test auswerten, ohne den Fernseher anzuschließen.

---

## 1. Hardware

- ESP32 (DevKit), LED-Streifen **WS2812B** oder kompatibel (5 V), 5-V-Netzteil
- Datenleitung: ESP32 **GPIO 16** (ESP32-C3: GPIO 4) → 330-Ω-Widerstand → **DIN** des Streifens. Die Weboberfläche lässt nur Pins zu, die den ESP32 nicht beschädigen können (keine Flash-, PSRAM- oder USB-Pins). Mit einem Pegelwandler (z. B. 74AHCT125) läuft es am zuverlässigsten, oft geht es aber auch ohne.
- **GND von Netzteil, ESP32 und Streifen verbinden.**
- Bei mehr als ca. 150 LEDs den Strom an beiden Enden einspeisen. Ein Kondensator mit 1000 µF am Streifen-Anfang schützt die LEDs.
- Wo der Streifen beginnt und in welche Richtung er läuft, ist egal, z. B. unten in der Mitte nahe am ESP32. Beides gleichst du in Schritt 6 in der Weboberfläche aus.

## 2. ESP32 flashen

1. Öffne in Chrome oder Edge **https://espressif.github.io/esptool-js/**.
2. Schließe den ESP32 per USB an, klicke auf **Connect** und wähle den Port.
3. Trage die Flash-Adresse **0x0** ein und wähle die Datei `AmbiSync-esp32-komplett.bin`. Bei einem ESP32-S3 oder -C3 nimmst du die passende Datei.
4. Klicke auf **Program** und warte, bis es fertig ist. Danach den ESP32 neu starten.

## 3. ESP32 einrichten

1. Die erste LED leuchtet rot. Das heißt: Einrichtungsmodus.
2. Verbinde dich mit dem WLAN **`AmbiSync-Setup`** (Passwort `ambisync-setup`). Die Einrichtungsseite öffnet sich von selbst, sonst http://192.168.4.1.
3. Heim-WLAN wählen, das WLAN-Passwort eingeben und ein **Admin-Passwort** (min. 6 Zeichen, Benutzer `admin`) festlegen. Ohne Admin-Passwort lässt sich nichts speichern. Der ESP32 startet neu und ist dann unter **http://ambisync.local** erreichbar, die IP steht auch in der FritzBox.
4. Unter **LED-Streifen** stellst du Pin, Anzahl, GRB und Strombegrenzung ein. Für 5 V/15 A sind etwa 13000 mA richtig. Dann **Speichern**.
5. Unter **Test** klickst du auf **Richtung prüfen**: Die erste LED wird rot, die zweite grün, jede zehnte blau.
6. In der FritzBox dem ESP32 **immer die gleiche IP** zuweisen. Zwingend ist das nicht, weil die TV-App ihn bei einer neuen IP von selbst wiederfindet.

Ist das Heim-WLAN beim Einschalten nicht erreichbar, etwa weil der Router nach einem Stromausfall noch startet, öffnet der ESP32 zusätzlich das Einrichtungs-WLAN, versucht aber weiter das Heim-WLAN und wechselt von selbst zurück. Auch im Einrichtungsmodus gilt dann das Admin-Passwort.

## Plug & Play (ab Firmware 1.1.0 / TV-App 1.1.0)

- **Finden sich selbst:** Der ESP32 meldet sich alle 2 s im Netz (UDP-Port 21327), solange kein Fernseher verbunden ist. Die TV-App hört mit, fragt zusätzlich per Broadcast (an 255.255.255.255 und an die Broadcast-Adresse jedes eigenen Netzes) und merkt sich die gefundene Adresse. Nach einem TV-Neustart ist die Verbindung damit sofort wieder da. Bekommt der ESP32 eine neue IP, zieht die App nach etwa 3 s mit.
- **LED-Anzahl live:** Änderst du am ESP32 die Anzahl der LEDs, teilt die TV-App ihre Zonen sofort neu auf, ohne Neustart.
- **Einbau-Assistent:** In der Weboberfläche Anfang und Laufrichtung des Streifens antippen (von vorne gesehen). Der ESP32 rechnet daraus „Start-LED“ und „Richtung umkehren“ aus und schickt beides an den Fernseher. „Am Fernseher prüfen“ zeigt das Kanten-Testbild.
- **Flüssig:** Der ESP32 misst, wie oft der Fernseher Bilder schickt, und blendet genau über diese Zeit zum nächsten Bild über (bei 25 Bildern/s gleichmäßig, bei der langsamen Samsung-Farbmessung weich an- und abbremsend). Szenenwechsel springen sofort. Zeitliches Dithering macht dunkle Übergänge feiner, als 8 Bit allein können.

Ports: 21324 Farben (WLED DRGB/DNRGB/WARLS), 4048 DDP, 21325 Status der TV-App, 21326 Suche, 21327 Selbstmeldung des ESP32.

## Qualität messen

`tools/measure/ambimeter.py` wertet ein Handy-Video aus (Fernseher und Wand im Bild) und liefert, wie schnell und wie weich das Licht dem Bild folgt. Anleitung und bisherige Messungen: [docs/QUALITAET.md](docs/QUALITAET.md).

## Bildmodi

In der Weboberfläche oben: **Film** (Standard, weich ohne spürbare Verzögerung), **Sport** (schneller, kräftigere Farben), **Spiel** (so direkt wie möglich), **Ruhig** (langsames Hintergrundlicht) und **Eigene** (Regler „Weiche Übergänge“ und eigene Werte). Der ESP32 stellt dafür sein Überblenden und die Reaktion/Sättigung der TV-App gemeinsam ein.

## 4. TV-App bauen

Der Ordner liegt in einem (privaten) GitHub-Repo. Bei jedem Push baut GitHub die App: unter **Actions → Build → Artifacts** findest du `AmbiSync-tv-app`. Bei einem Tag `v1.0.0` entsteht zusätzlich ein **Release**.

Die App findet den ESP32 von selbst. Es muss also **keine IP** in der App eingetragen werden.

## 5. TV-App installieren (Partner-Zertifikat)

Die App liest Samsung-interne Bildquellen. Das erlaubt der Fernseher nur Apps mit einem **Partner-Zertifikat** für genau dieses Gerät.

**Weg A, mit dem TizenBrew Installer (einfach):**
Der Installer erstellt bei Tizen 7 und neuer über dein Samsung-Konto ein Partner-Zertifikat. Wenn im Repo ein Release existiert, im Installer `DEIN-GITHUB-NAME/ambisync` eingeben. Er lädt die `.tpk` aus dem neuesten Release, signiert sie mit deinem Zertifikat und installiert sie. Danach den Fernseher einmal komplett neu starten (Stecker ziehen).

**Weg B, mit Tizen Studio (offizielles Samsung-Werkzeug):**
1. Tizen Studio (CLI) mit *Samsung Certificate Extension* installieren.
2. Den Fernseher im Entwicklermodus verbinden: `sdb connect <TV-IP>`.
3. Im Certificate Manager ein **Samsung**-Zertifikat anlegen, Privilege-Level **Partner**, mit der DUID des Fernsehers.
4. `tizen package -t tpk -s <profilname> -- AmbiSync-default-cert.tpk`
5. `tizen install -n AmbiSync-default-cert.tpk -t <gerätename>`
6. Den Fernseher neu starten.

Die App hat kein Symbol. Sie läuft unsichtbar im Hintergrund und startet mit dem Fernseher.

## 6. Feinabstimmung (Weboberfläche des ESP32)

- **Fernseher-App** zeigt Bildquelle, Bilder pro Sekunde und Menü-Erkennung. Steht dort nach einem Neustart des Fernsehers „verbunden“, läuft alles.
- **Mapping:** Setze unter *Fernseher-Einstellungen* das *Kanten-Testbild vom TV* auf „an“. Richtig ist: oben rot, rechts grün, unten blau, links gelb. Wenn nicht, mit *Start-LED verschieben* und *Laufrichtung umkehren* korrigieren und das Testbild wieder ausschalten.
- **Menü & Startseite:**
  - *automatisch* (Standard) erkennt selbst, ob die Menü-Ebene Transparenz hat.
  - Wenn Menüs nicht ankommen, *Transparenz-Modus* oder *Dunkel-Modus* ausprobieren.
  - *aus* nutzt nur das Video, wie das Original.

So liest du die Diagnose unter „Menü-Erkennung“:

| Anzeige | Bedeutung |
|---|---|
| „Transparenz erkannt“ | Die Menü-Ebene ist dort durchsichtig, wo das Video läuft. Der beste Fall. |
| „nicht aktiv: … Operation not permitted“ | Die App ist nicht mit einem Partner-Zertifikat signiert. |
| Menü-Anteil bleibt bei 0 % | Die UI-Ebene liefert auf deinem Modell nichts Brauchbares. Dann funktioniert nur das Video. |

## Sicherheit und Datenschutz

- Der Fernseher schickt **nur Zonenfarben** (ein paar hundert Bytes) und ein kleines Statuspaket mit Zahlen an den ESP32. Es werden **keine Bilder** übertragen, auch nicht ins Internet.
- Die Menü-Aufnahme liegt nur Millisekunden als Datei im App-Ordner und wird sofort gelöscht.
- Aus dem Original entfernt: die Funktion, die ein komplettes Bild an einen Server schickt. Den Aufnahmeweg „Datei“ kann der Steuerkanal nicht mehr umschalten.
- Der ESP32 gibt an den Fernseher nur geprüfte Zahlenwerte weiter.
- Unter **Farben nur von dieser IP annehmen** trägst du die IP des Fernsehers ein. Dann kann kein anderes Gerät die LEDs steuern oder Statuspakete einschleusen.
- Die Weboberfläche ist geschützt durch: Admin-Passwort (Pflicht), Prüfung des Hostnamens (gegen DNS-Rebinding) und einen eigenen Header bei jeder Änderung. Dadurch können fremde Webseiten im Browser nichts am ESP32 verstellen.
- An den Fernseher gibt der ESP32 nur Einstellungen aus einer festen Liste weiter, z. B. Helligkeit, Mapping oder Menü-Modus. Befehle wie Beenden oder den Aufnahmeweg umschalten sind nicht dabei.
- Die TV-App macht bei Pause, Standby und beim Beenden keine Aufnahmen.
- Es gilt dieselbe Annahme wie beim Original: Das Heimnetz ist vertrauenswürdig. Die UDP-Protokolle haben keine Verschlüsselung.

## Deinstallieren

- TV-App: `tizen uninstall -p de.mehrdad.AmbiSync` oder über den TizenBrew Installer bzw. Apps2Samsung.
- Danach den Entwicklermodus am Fernseher ausschalten (Apps → 12345 → Off).

## Lizenz

MIT. Die TV-App basiert auf TizenAmbilight von Ritwick Verma (MIT, siehe `tv-app/LICENSE` und `tv-app/UPSTREAM-README.md`).

## Weboberfläche ändern

Die Seite liegt in `esp32-firmware/webui-src/index.html`. Nach einer Änderung `python3 esp32-firmware/tools/make_webui.py` ausführen. Das Skript erzeugt `AmbiSync/webui.h` als gzip-komprimierte Byte-Liste. Als Text-Konstante würde die Arduino IDE JavaScript-Zeilen wie `async function refresh(){` für C++-Funktionen halten und Code mitten in die Seite schreiben.

## Bluetooth-Test (ab 1.6)

Statt per WLAN kann der Fernseher die Farben per Bluetooth LE zum ESP32 schicken. Ab Werk aus; einschalten in der
Weboberfläche (Übersicht → Fernseher-App → „Bluetooth-Test starten“, der ESP32 startet neu). Die TV-App verbindet sich
dann von selbst, schickt jedes Bild in 2 Bluetooth-Paketen (WLED-DNRGB) und nimmt automatisch wieder WLAN, wenn
Bluetooth nicht klappt. Weboberfläche, Einstellungen und Updates laufen weiter über WLAN.

- Firmware ab 1.5 braucht mehr Platz: in der Arduino IDE **Werkzeuge → Partition Scheme → „Minimal SPIFFS (1.9MB APP
  with OTA)“** wählen und einmal per USB flashen. Ein Update über die Weboberfläche von einer älteren Firmware passt nicht.
- Bluetooth und WLAN teilen sich im ESP32 eine Antenne; mit Bluetooth an läuft das WLAN im Stromsparmodus.
- Ob der Fernseher Bluetooth für Apps freigibt, zeigt die Zeile „Bluetooth am TV“.

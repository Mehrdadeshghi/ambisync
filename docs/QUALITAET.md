# Bildqualität messen

Jede Version muss gegen die vorige gemessen werden, nicht nach Gefühl. Dafür gibt es
`tools/measure/ambimeter.py`: Handy-Video rein, Zahlen raus.

## Aufnahme

- Handy fest hinstellen oder anlehnen, Fernseher **und** die beleuchtete Wand rundherum im Bild.
- Belichtung sperren (iPhone: lange auf den Bildschirm tippen, bis „AE/AF-Sperre“ erscheint). Sonst regelt
  die Kamera bei jedem Szenenwechsel nach und verfälscht die Werte.
- 20–60 s eines Films mit mehreren harten Schnitten. Zimmer möglichst dunkel.
- Am besten zweimal: einmal mit Inhalt **ohne** Kopierschutz (TV-Programm, USB-Stick, HDMI), einmal mit
  geschütztem Inhalt (Streaming-App).

## Auswertung

```bash
python3 tools/measure/ambimeter.py video.mov --preview            # Bild mit Raster, Ecken ablesen
python3 tools/measure/ambimeter.py video.mov --screen "x1,y1 x2,y2 x3,y3 x4,y4" --label 1.2.5
```

Ecken = die vier Ecken des leuchtenden Bildes in Pixeln der aufrechten Aufnahme (oben links, oben rechts,
unten rechts, unten links). Ausgabe: `.md` (Tabelle), `.json`, `.png` (Verlauf Bildrand gegen Wand je Seite).

## Wie es andere machen (und AmbiSync seit 1.4)

Verglichen mit Hyperion.ng und HyperHDR, den verbreitetsten Ambilight-Projekten:

| | Hyperion.ng | HyperHDR | AmbiSync bis 1.3 | AmbiSync ab 1.4 |
|---|---|---|---|---|
| Wo wird geglättet? | einmal, am Ausgang | einmal, am Ausgang | zweimal (TV-App **und** ESP32) | einmal, im ESP32 |
| Verfahren | linear, 150 ms, 25 Hz | gedämpfte Feder (stiffness 150, damping 26), 150 ms, 50 Hz | Überblenden je Bild neu gestartet | kritisch gedämpfte Feder, 60 Hz, exakt gerechnet |
| neues Bild während einer Bewegung | Ziel wird ersetzt | Ziel wird ersetzt, Geschwindigkeit bleibt | Überblenden startete neu | Ziel wird ersetzt, Geschwindigkeit bleibt |
| Helligkeitskurve | fest | fest | passte sich je Szene an | fest (ag=0) |
| dunkle Töne | Dithering | Flacker-Filter (½ Stufe Hysterese) | Dithering | Flacker-Filter |
| Ersatzquelle (DRM) | – | – | Punkte einzeln aufgefrischt (Mischbilder) | nur vollständige Umläufe |

Quellen: `schema-smoothing.json` von Hyperion.ng (type linear, time_ms 150, updateFrequency 25) und HyperHDR
(HybridRgbInterpolator, time_ms 150, updateFrequency 50, stiffness 150, damping 26, antiFlickeringFilter),
`InfiniteHybridRgbInterpolator.cpp` und `LedDevice.cpp` (Flacker-Filter) aus dem HyperHDR-Quellcode (MIT).

## TV-App: Bildauswertung im Vergleich (ab 1.5)

| | HyperHDR / Hyperion.ng | AmbiSync bis 1.4 | AmbiSync ab 1.5 |
|---|---|---|---|
| Helligkeitskurve | fest, gamma 1,5 | passte sich je Szene an (ESP schaltete sie ab, die TV-App allein nicht) | fest, gamma 1,5 |
| Balken oben/unten (Letterbox) | an | aus | an |
| neue Balken gelten nach | 50 gleichen Bildern (borderFrameCnt) | 8 Bildern | 48 Bildern (~2 s) |
| einzelne Ausreißer (heller Schnitt, Untertitel) | bis 10 Bilder ignoriert | Zähler fing neu an | bis 10 Bilder ignoriert |
| schwarzes Bild (Abblende) | „unbekannt“, ändert nichts (erst nach 600 Bildern) | galt als „keine Balken“, danach oben/unten kurz dunkel | „unbekannt“, ändert nichts (erst nach 600 Bildern) |
| weicher Rand am Balken | +1 px (blurRemoveCnt) | – | +2 px (Bild ist 960×540) |
| Aufnahmetakt | gleichmäßig | ab 0,4 s Standbild nur noch ⅓ | erst ab 5 s Standbild (Pause/Menü) gedrosselt |
| Mittelwert je Zone | in linearem Licht | im Code-Bereich (YUV) | unverändert; Zonen sind seit 253 LEDs klein, linear wäre auf dem TV ~8× langsamer (gemessen) |
| Glätten | einmal, am Ausgang | ESP32 (TV liefert roh) | unverändert |

Quellen: `BlackBorderProcessor.cpp`, `BlackBorderDetector.cpp`, `ImageColorAveraging.cpp`, `InfiniteProcessing.cpp` und
`schema-blackborderdetector.json` aus HyperHDR (MIT). HyperTizen (Samsung, Tizen) nutzt nur die Ersatzquelle mit 16
Messpunkten und überlässt alles Weitere Hyperion/HyperHDR.

## Die Maße

| Maß | Bedeutung | Ziel |
|---|---|---|
| **Schnitt → 50 %** | Zeit nach einem harten Schnitt, bis das Wandlicht halb bei der neuen Farbe ist | < 150 ms (Video-Ebene) |
| **Schnitt → 90 %** | dasselbe bis 90 % | < 250 ms (Video-Ebene) |
| **Stufigkeit** | größter Anteil der Änderung innerhalb von 50 ms (1 = harter Sprung, 0,3 = gleichmäßig über 160 ms) | 0,25–0,4 |
| **Unruhe** | Zittern der Wandfarbe, während das Bild ruhig ist | so klein wie möglich |
| Verzögerung / Farbfolge | Korrelation Bildrand ↔ Wand. **Nur Richtwert:** der automatische Weißabgleich der Kamera verändert Bild und Wand gleichzeitig und zieht die Werte Richtung „0 ms / perfekt“ | – |

Die Schnitt-Werte sind die verlässlichen: Sie messen genau das, was man sieht, wenn das Licht hinterherhinkt.

## Messungen

Samsung GQ65Q7F, 300 WS2812B, Inhalt jeweils mit Kopierschutz (Ersatzquelle „ppi“, Video-Ebene rc=-4).

| Video | Version | Quelle | Schnitt → 50 % | Schnitt → 90 % | Stufigkeit | Unruhe |
|---|---|---|---|---|---|---|
| IMG_0696 | 1.2.1 | ppi, 24 Punkte, 40 ms | 933 ms | 1067 ms | 0,26 | 0,65 |
| IMG_0697 | 1.2.1 | ppi, 24 Punkte, 40 ms | 767 ms | 883 ms | 0,46 | 0,72 |
| IMG_0698 | 1.2.3 | ppi, 12 Punkte, 20 ms | **375 ms** | **600 ms** | 0,32 | 1,58 |

Erwartung für 1.4 (Feder im Modus Film): 50 % nach ~125 ms, 90 % nach ~285 ms ab dem Moment, in dem das
Bild beim ESP32 ankommt (gerechnet). Bei der Ersatzquelle kommt die Zeit bis zum nächsten vollständigen Umlauf
dazu. Noch offen: Messung mit 1.4 und eine Messung mit der Video-Ebene (Inhalt ohne Kopierschutz).

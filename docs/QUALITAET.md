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

Noch offen: Messung mit 1.2.5 und eine Messung mit der Video-Ebene (Inhalt ohne Kopierschutz).

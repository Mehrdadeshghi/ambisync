using System;

namespace TizenAmbilight
{
    /// <summary>
    /// AmbiSync: entscheidet, wann ein erkannter Balken (Letterbox oben/unten oder Pillarbox links/rechts)
    /// wirklich übernommen wird. Nachgebaut nach HyperHDR/Hyperion.ng (BlackBorderProcessor::updateBorder, MIT):
    ///
    ///  - Eine neue Balkengröße gilt erst, wenn sie <see cref="SwitchFrames"/> Bilder in Folge gleich gemessen wurde
    ///    (HyperHDR: borderFrameCnt 50).
    ///  - Bis zu <see cref="MaxInconsistent"/> abweichende Einzelbilder (Explosion, heller Schnitt, Untertitel)
    ///    setzen den Zähler NICHT zurück (HyperHDR: maxInconsistentCnt 10). Vorher fing jeder Ausreißer von vorn an.
    ///  - Ein ganz schwarzes Bild ist "unbekannt", nicht "keine Balken". Es ändert nichts, außer es dauert
    ///    <see cref="UnknownFrames"/> Bilder (HyperHDR: unknownFrameCnt 600). Vorher verlor jede Abblende auf Schwarz
    ///    die Balken, und nach dem Aufblenden waren oben/unten kurz dunkel.
    ///  - Solange noch nie etwas Sicheres erkannt wurde, gilt die erste stabile Messung sofort.
    ///
    /// Werte sind Pixel im Aufnahmebild: <see cref="Start"/> = erste Bildzeile/-spalte, <see cref="End"/> = eine hinter der letzten.
    /// </summary>
    public sealed class BorderTracker
    {
        public int SwitchFrames = 50;
        public int MaxInconsistent = 10;
        public int UnknownFrames = 600;
        public int Tolerance = 2;          // ±px gelten als gleich (skaliertes Bild rauscht um 1–2 px)

        public int Start { get; private set; }
        public int End { get; private set; }
        public bool Known { get; private set; }   // false = volle Fläche, weil (noch) nichts Sicheres bekannt

        bool _prevUnknown = true;
        int _prevStart = -1, _prevEnd = -1;
        int _consistent, _inconsistent;

        public BorderTracker(int full) { Reset(full); }

        public void Reset(int full)
        {
            Start = 0; End = full; Known = false;
            _prevUnknown = true; _prevStart = -1; _prevEnd = -1;
            _consistent = 0; _inconsistent = 0;
        }

        bool Same(bool u1, int s1, int e1, bool u2, int s2, int e2)
        {
            if (u1 || u2) return u1 == u2;
            return Math.Abs(s1 - s2) <= Tolerance && Math.Abs(e1 - e2) <= Tolerance;
        }

        /// <summary>Eine Messung einspeisen. true = übernommene Fläche hat sich geändert.</summary>
        public bool Update(bool unknown, int start, int end, int full)
        {
            if (Same(unknown, start, end, _prevUnknown, _prevStart, _prevEnd))
            {
                _consistent++;
                _inconsistent = 0;
            }
            else
            {
                _inconsistent++;
                if (_inconsistent <= MaxInconsistent) return false;   // kurzer Ausreißer: verwerfen
                _prevUnknown = unknown; _prevStart = start; _prevEnd = end;
                _consistent = 0;
            }

            // schon übernommen?
            if (Same(unknown, start, end, !Known, Start, End)) { _inconsistent = 0; return false; }

            if (unknown)
            {
                if (_consistent == UnknownFrames) { Start = 0; End = full; Known = false; return true; }
                return false;
            }
            if (!Known || _consistent == SwitchFrames)
            {
                Start = start; End = end; Known = true;
                return true;
            }
            return false;
        }
    }
}

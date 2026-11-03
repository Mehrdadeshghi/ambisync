using System;
using System.IO;
using System.Threading;

namespace TizenAmbilight
{
    /// <summary>
    /// AmbiSync: Menü, Startseite und App-Oberflächen in das Ambilight einrechnen.
    ///
    /// Der Fernseher hat zwei getrennte Bildebenen:
    ///   * die VIDEO-Ebene (Filme, Live-TV) - liest <see cref="VideoPlaneCapture"/>,
    ///   * die BILDSCHIRM-/UI-Ebene (Menüs, Startseite, App-Oberflächen) - liest
    ///     dc_request_capture_to_file_sync (<see cref="CaptureApi"/>) als RGBA-PNG.
    /// Das Original nutzt nur eine von beiden. Hier läuft die UI-Ebene in einem eigenen Thread mit
    /// wenigen Bildern pro Sekunde mit, und die Hauptschleife legt sie zonenweise über das Video:
    ///
    ///     farbe = w * ui + (1 - w) * video        (w = Deckkraft der UI in dieser Zone, 0..1)
    ///
    /// Wie die Deckkraft bestimmt wird, hängt davon ab, was die UI-Aufnahme liefert. Das ist auf dem
    /// Gerät nicht dokumentiert, darum gibt es drei Modi:
    ///   * Alpha:      der Alphakanal der PNG ist echt (durchsichtig, wo das Video durchscheint).
    ///   * Nicht-Schwarz: die PNG ist überall deckend und dort schwarz, wo das Video liegt.
    ///   * Auto:       Alpha, sobald in einer Aufnahme deutlich durchsichtige Pixel vorkommen,
    ///                 sonst Nicht-Schwarz.
    ///
    /// Datenschutz: Die Aufnahme liegt nur kurz als Datei im eigenen Datenordner der App und wird
    /// sofort nach dem Auswerten gelöscht. Es verlassen nur Zonenfarben das Gerät (an den ESP32).
    /// </summary>
    static class UiOverlay
    {
        public const int ModeOff = 0, ModeAuto = 1, ModeAlpha = 2, ModeNonBlack = 3;

        // ---- Einstellungen (von der Hauptschleife/dem Steuerkanal gesetzt) ----
        public static volatile int Mode = ModeAuto;
        public static volatile int Fps = 6;                // UI-Aufnahmen pro Sekunde
        public static volatile int BlackThresh = 24;       // Nicht-Schwarz-Modus: Luma darunter = "Loch"
        public static volatile int StaleMs = 5000;         // UI unverändert, Video läuft -> UI ausblenden (0 = nie)
        public const int MaxAgeMs = 1500;                  // ältere UI-Daten werden ignoriert

        // ---- Ergebnis der letzten Aufnahme (unter _lock) ----
        static readonly object _lock = new object();
        static byte[] _r, _g, _b;
        static double[] _w;
        static int _count;
        static DateTime _frameUtc = DateTime.MinValue;
        static DateTime _changeUtc = DateTime.MinValue;
        static int _sig;

        // ---- Status für die Anzeige auf dem ESP32 ----
        public static volatile bool Running;
        public static volatile string LastError;
        public static long Frames, Fails;
        public static volatile int LastW, LastH;
        public static double LastCoverage;      // mittlere Deckkraft der letzten Aufnahme (0..1)
        public static double LastTransparent;   // Anteil durchsichtiger Pixel (Alpha < 250)
        public static double LastNonBlack;      // Anteil nicht-schwarzer Pixel
        public static volatile bool AlphaSeen;  // jemals echte Transparenz gesehen?
        public static double LastCaptureMs, LastDecodeMs;
        public static double AppliedCoverage;   // was zuletzt tatsächlich eingemischt wurde

        /// <summary>Von der Hauptschleife gesetzt: true = gerade keine Native-Aufrufe (Pause, Standby, Beenden).</summary>
        public static volatile Func<bool> ShouldIdle;
        static volatile bool _inNative;
        static volatile bool _parked;
        static bool _staleLatched;   // unter _lock: wegen "eingefroren" ausgeblendet, bis sich die UI ändert

        /// <summary>Wartet (max. 2 s), bis kein Aufnahme-Aufruf mehr läuft. true = ruhig.</summary>
        public static bool Park()
        {
            _parked = true;
            for (int i = 0; i < 40 && _inNative; i++) Thread.Sleep(50);
            return !_inNative;
        }

        static Thread _thread;
        static string _dir;
        static volatile int _zoneCount;
        static double _bandFrac;

        public static string ModeName(int m)
        {
            return m == ModeOff ? "off" : m == ModeAlpha ? "alpha" : m == ModeNonBlack ? "nonblack" : "auto";
        }

        /// <summary>Neue Zonenzahl (LED-Anzahl am ESP32 geändert); der Sampler baut seine Zonen selbst neu.</summary>
        public static void SetZoneCount(int zoneCount) { if (zoneCount >= 4) _zoneCount = zoneCount; }

        /// <summary>Startet den Sampler-Thread (einmalig).</summary>
        public static void Start(string captureDir, int zoneCount, double bandFrac)
        {
            if (_thread != null) return;
            _dir = captureDir;
            _zoneCount = zoneCount;
            _bandFrac = bandFrac;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ui-overlay" };
            _thread.Start();
        }

        static void Loop()
        {
            const string file = "ui.png";
            ZoneLayout layout = null;
            int fails = 0;
            while (true)
            {
                int mode = Mode;
                Func<bool> idle = ShouldIdle;
                bool mustIdle = idle != null && idle();
                if (!mustIdle) _parked = false;
                if (mode == ModeOff || !CaptureApi.Ready || mustIdle || _parked)
                {
                    Running = false;
                    Thread.Sleep(mustIdle ? 500 : 1000);
                    continue;
                }
                Running = true;
                int periodMs = 1000 / Math.Max(1, Math.Min(15, Fps));
                DateTime t0 = DateTime.UtcNow;
                string path = null;
                try
                {
                    var csw = System.Diagnostics.Stopwatch.StartNew();
                    CaptureResult res;
                    _inNative = true;
                    try
                    {
                        res = CaptureApi.Capture(_dir, file, CaptureApi.RequestWidth, CaptureApi.RequestHeight,
                                                 CaptureApi.CompressPng, CaptureApi.JpegQuality);
                    }
                    finally { _inNative = false; }
                    csw.Stop();
                    LastCaptureMs = csw.Elapsed.TotalMilliseconds;
                    path = res.RetPath;
                    if (!res.HasFrame)
                    {
                        Interlocked.Increment(ref Fails);
                        LastError = res.Status;
                        fails++;
                        // Bildschirm aus / nichts zu sehen: langsamer nachfragen
                        Thread.Sleep(Math.Min(2000, 200 * fails));
                        continue;
                    }
                    var dsw = System.Diagnostics.Stopwatch.StartNew();
                    PngImage img = PngDecoder.DecodeFile(res.RetPath);
                    dsw.Stop();
                    LastDecodeMs = dsw.Elapsed.TotalMilliseconds;
                    fails = 0;
                    // Gleiche Kantenaufteilung wie die Video-Ebene (Seitenverhältnis der aktuellen
                    // Video-Anfrage), sonst landen Menüfarben auf anderen LEDs als Videofarben.
                    int refW = VideoPlaneCapture.ReqW, refH = VideoPlaneCapture.ReqH;
                    if (layout == null || LastW != img.Width || LastH != img.Height || layout.Total != _zoneCount ||
                        _refW != refW || _refH != refH)
                    {
                        layout = ScaledLayout(refW, refH, img.Width, img.Height, _zoneCount, _bandFrac);
                        LastW = img.Width; LastH = img.Height; _refW = refW; _refH = refH;
                    }
                    Analyse(img, layout, mode);
                    Interlocked.Increment(ref Frames);
                    LastError = null;
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref Fails);
                    LastError = e.GetType().Name + ": " + e.Message;
                    Thread.Sleep(500);
                }
                finally
                {
                    // Keine Bildschirmfotos auf dem Gerät liegen lassen.
                    TryDelete(path);
                    if (_dir != null) TryDelete(Path.Combine(_dir, file));
                }
                int spent = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                if (spent < periodMs) Thread.Sleep(periodMs - spent);
            }
        }

        static int _refW, _refH;

        /// <summary>Zonen für ein refW×refH-Bild bauen (wie die Video-Ebene) und auf w×h umrechnen.</summary>
        internal static ZoneLayout ScaledLayout(int refW, int refH, int w, int h, int total, double band)
        {
            var l = new ZoneLayout(refW, refH, total, band);
            foreach (Zone z in l.Zones)
            {
                z.X0 = (int)((long)z.X0 * w / refW); z.X1 = (int)((long)z.X1 * w / refW);
                z.Y0 = (int)((long)z.Y0 * h / refH); z.Y1 = (int)((long)z.Y1 * h / refH);
                if (z.X1 <= z.X0) z.X1 = Math.Min(w, z.X0 + 1);
                if (z.Y1 <= z.Y0) z.Y1 = Math.Min(h, z.Y0 + 1);
            }
            return l;
        }

        /// <summary>Millisekunden seit sich die UI zuletzt sichtbar verändert hat.</summary>
        public static double MsSinceChange
        {
            get { lock (_lock) { return _changeUtc == DateTime.MinValue ? double.MaxValue : (DateTime.UtcNow - _changeUtc).TotalMilliseconds; } }
        }

        static void TryDelete(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>Zonenfarben und Deckkraft aus einer RGBA-Aufnahme.</summary>
        internal static void Analyse(PngImage img, ZoneLayout layout, int mode)
        {
            byte[] px = img.Rgba;
            int w = img.Width, h = img.Height;

            // ---- globale Statistik (jedes 4. Pixel genügt) ----
            long n = 0, transp = 0, nonBlack = 0;
            int thr = BlackThresh;
            for (int i = 0; i + 3 < px.Length; i += 16)
            {
                n++;
                if (px[i + 3] < 250) transp++;
                int y = (54 * px[i] + 183 * px[i + 1] + 19 * px[i + 2]) >> 8;
                if (y > thr) nonBlack++;
            }
            double fTransp = n > 0 ? (double)transp / n : 0;
            double fNonBlack = n > 0 ? (double)nonBlack / n : 0;
            LastTransparent = fTransp;
            LastNonBlack = fNonBlack;
            if (fTransp > 0.02) AlphaSeen = true;

            bool useAlpha = mode == ModeAlpha || (mode == ModeAuto && AlphaSeen);

            int count = layout.Total;
            var r = new byte[count]; var g = new byte[count]; var b = new byte[count];
            var wt = new double[count];
            double covSum = 0;
            for (int z = 0; z < count; z++)
            {
                Zone zn = layout.Zones[z];
                long sr = 0, sg = 0, sb = 0, sw = 0, cnt = 0;
                for (int yy = zn.Y0; yy < zn.Y1; yy += 2)
                {
                    int row = yy * w;
                    for (int xx = zn.X0; xx < zn.X1; xx += 2)
                    {
                        int i = (row + xx) * 4;
                        int pr = px[i], pg = px[i + 1], pb = px[i + 2], pa = px[i + 3];
                        int weight;
                        if (useAlpha) weight = pa;
                        else
                        {
                            int y = (54 * pr + 183 * pg + 19 * pb) >> 8;
                            weight = y > thr ? 255 : 0;
                        }
                        sr += pr * weight; sg += pg * weight; sb += pb * weight; sw += weight;
                        cnt++;
                    }
                }
                if (sw > 0)
                {
                    r[z] = (byte)Math.Min(255, sr / sw);
                    g[z] = (byte)Math.Min(255, sg / sw);
                    b[z] = (byte)Math.Min(255, sb / sw);
                }
                wt[z] = cnt > 0 ? sw / (255.0 * cnt) : 0;
                covSum += wt[z];
            }
            LastCoverage = count > 0 ? covSum / count : 0;

            // Änderungserkennung (5 Bit pro Kanal, damit Rauschen nicht als Änderung zählt)
            int sig = 17;
            for (int z = 0; z < count; z++)
                sig = unchecked(sig * 31 + (r[z] >> 3) * 1024 + (g[z] >> 3) * 32 + (b[z] >> 3) + (int)(wt[z] * 8) * 32768);

            lock (_lock)
            {
                _r = r; _g = g; _b = b; _w = wt; _count = count;
                _frameUtc = DateTime.UtcNow;
                if (sig != _sig) { _sig = sig; _changeUtc = _frameUtc; _staleLatched = false; }
            }
        }

        /// <summary>Gibt es eine aktuelle UI-Aufnahme mit dieser Zonenzahl?</summary>
        public static bool HasFresh(int zoneCount)
        {
            lock (_lock)
            {
                return Mode != ModeOff && _w != null && _count == zoneCount &&
                       (DateTime.UtcNow - _frameUtc).TotalMilliseconds <= MaxAgeMs;
            }
        }

        /// <summary>Nur UI (kein Videobild verfügbar, z. B. Startseite): Zonenfarben mit voller Deckkraft
        /// in <paramref name="dst"/> schreiben. Durchsichtige/schwarze Zonen bleiben dunkel.</summary>
        public static bool CopyUiOnly(ZoneColors dst, bool protectedVideo)
        {
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                if (_w == null || _count != dst.Count || (now - _frameUtc).TotalMilliseconds > MaxAgeMs)
                    return false;
                // Läuft ein geschützter Film (DRM) und die UI steht still, ist sie nur ein Rest hinter dem
                // Film: ausblenden statt den Streifen den ganzen Film auf einer Farbe zu halten.
                double keep = 1.0;
                int staleMs = StaleMs;
                if (protectedVideo && staleMs > 0)
                {
                    double age = (now - _changeUtc).TotalMilliseconds;
                    if (age > staleMs) keep = Math.Max(0.0, 1.0 - (age - staleMs) / 1000.0);
                }
                for (int z = 0; z < _count; z++)
                {
                    double a = _w[z] * keep;
                    dst.R[z] = (byte)(a * _r[z] + 0.5);
                    dst.G[z] = (byte)(a * _g[z] + 0.5);
                    dst.B[z] = (byte)(a * _b[z] + 0.5);
                }
                AppliedCoverage = LastCoverage * keep;
                return true;
            }
        }

        /// <summary>UI über das Video legen, in place. <paramref name="videoLive"/> = das Videobild
        /// ändert sich gerade. Liefert die eingemischte mittlere Deckkraft (0 = nichts eingemischt).</summary>
        public static double BlendInto(ZoneColors video, bool videoLive)
        {
            if (Mode == ModeOff) { AppliedCoverage = 0; return 0; }
            lock (_lock)
            {
                if (_w == null || _count != video.Count) { AppliedCoverage = 0; return 0; }
                DateTime now = DateTime.UtcNow;
                if ((now - _frameUtc).TotalMilliseconds > MaxAgeMs) { AppliedCoverage = 0; return 0; }

                // Eine UI, die sich lange nicht ändert, während das Video läuft, ist sehr wahrscheinlich
                // nur ein eingefrorener Rest (z. B. die App-Oberfläche hinter dem Film). Langsam ausblenden.
                double keep = 1.0;
                int staleMs = StaleMs;
                if (staleMs > 0 && (videoLive || _staleLatched))
                {
                    double age = (now - _changeUtc).TotalMilliseconds;
                    if (age > staleMs) keep = Math.Max(0.0, 1.0 - (age - staleMs) / 1000.0);
                    if (keep <= 0.0) _staleLatched = true;
                }
                if (keep <= 0.0) { AppliedCoverage = 0; return 0; }

                double sum = 0;
                for (int z = 0; z < _count; z++)
                {
                    double a = _w[z] * keep;
                    if (a <= 0.004) continue;
                    double ia = 1.0 - a;
                    video.R[z] = (byte)(a * _r[z] + ia * video.R[z] + 0.5);
                    video.G[z] = (byte)(a * _g[z] + ia * video.G[z] + 0.5);
                    video.B[z] = (byte)(a * _b[z] + ia * video.B[z] + 0.5);
                    sum += a;
                }
                AppliedCoverage = _count > 0 ? sum / _count : 0;
                return AppliedCoverage;
            }
        }
    }
}

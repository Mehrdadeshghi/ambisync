using System;
using System.Diagnostics;
using System.Threading;

namespace TizenAmbilight
{
    /// <summary>Die eigentliche Bluetooth-Verbindung (auf dem TV: TizenFX, in den Tests: Attrappe).</summary>
    interface IBleTransport
    {
        bool Connect(string mac, out string err);
        int Mtu { get; }
        bool Connected { get; }
        bool Write(byte[] data, out string err);
        void Close();
    }

    /// <summary>
    /// AmbiSync (Test): Farben per Bluetooth LE statt per WLAN zum ESP32.
    ///
    /// Der ESP32 schaltet es über den Steuerkanal ein (ble=1, blemac=Adresse als Zahl). Ein eigener Faden
    /// verbindet sich und schickt immer nur das NEUESTE Bild (ältere werden verworfen, nichts staut sich).
    /// Ein Bild wird in WLED-DNRGB-Stücke zerlegt, die je in einen Bluetooth-Schreibvorgang passen
    /// (MTU − 3 Byte); der ESP32 behandelt sie wie ein UDP-Paket. Klappt etwas nicht, gilt <see cref="Active"/>
    /// = false und die Render-Schleife schickt wie bisher per WLAN.
    /// </summary>
    static partial class BleLink
    {
        public static volatile bool Wanted;
        public static long MacNum;                 // 48-bit-Adresse als Zahl (Steuer-Tokens sind nur Zahlen)

        public static volatile string State = "off";   // off | connecting | connected | failed
        public static volatile string Err = "";
        public static int Mtu;
        public static long Frames, Writes, Fails, Connects;
        public static double LastFrameMs, Fps;

        static readonly object _lock = new object();
        static byte[] _pending;          // neuestes Bild (DRGB-Paket), wartet auf den Faden
        static int _pendingLen;
        static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        static Thread _thread;
        static DateTime _lastOkUtc = DateTime.MinValue;
        static IBleTransport _tx;

        /// <summary>true = gerade per Bluetooth verbunden und zuletzt erfolgreich geschrieben.</summary>
        public static bool Active
        {
            get { return Wanted && State == "connected" && (DateTime.UtcNow - _lastOkUtc).TotalMilliseconds < 1500; }
        }

        public static string MacString(long n)
        {
            var b = new char[17];
            for (int i = 0; i < 6; i++)
            {
                int v = (int)((n >> (8 * (5 - i))) & 0xFF);
                b[i * 3] = "0123456789ABCDEF"[v >> 4];
                b[i * 3 + 1] = "0123456789ABCDEF"[v & 15];
                if (i < 5) b[i * 3 + 2] = ':';
            }
            return new string(b);
        }

        /// <summary>Steuer-Token vom ESP32.</summary>
        public static void SetWanted(bool on)
        {
            if (on == Wanted) return;
            Wanted = on;
            Ambilight.L("bluetooth: " + (on ? "ESP32 wünscht Bluetooth (" + MacString(MacNum) + ")" : "aus, Farben per WLAN"));
            if (on) EnsureThread();
            _signal.Set();
        }

        static void EnsureThread()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "ble-link" };
            _thread.Start();
        }

        /// <summary>Neues Bild anbieten (DRGB-Paket aus WledOutput). Kopiert und kehrt sofort zurück.</summary>
        public static void Offer(byte[] drgb, int len)
        {
            lock (_lock)
            {
                if (_pending == null || _pending.Length < len) _pending = new byte[len];
                Buffer.BlockCopy(drgb, 0, _pending, 0, len);
                _pendingLen = len;
            }
            _signal.Set();
        }

        /// <summary>DRGB [2, t, rgb...] in DNRGB-Stücke [4, t, startHi, startLo, rgb...] zerlegen, je höchstens
        /// <paramref name="maxBytes"/> Byte. Reine Funktion (getestet).</summary>
        public static byte[][] Chunks(byte[] drgb, int len, int maxBytes)
        {
            if (len < 2) return new byte[0][];
            int leds = (len - 2) / 3;
            int per = Math.Max(1, (maxBytes - 4) / 3);
            int n = (leds + per - 1) / per;
            var outp = new byte[n][];
            for (int c = 0; c < n; c++)
            {
                int start = c * per;
                int cnt = Math.Min(per, leds - start);
                var b = new byte[4 + cnt * 3];
                b[0] = 4; b[1] = drgb[1];
                b[2] = (byte)(start >> 8); b[3] = (byte)(start & 0xFF);
                Buffer.BlockCopy(drgb, 2 + start * 3, b, 4, cnt * 3);
                outp[c] = b;
            }
            return outp;
        }

        static void Run()
        {
            DateTime nextTry = DateTime.MinValue;
            int failRun = 0;
            long fpsFrames = 0; DateTime fpsT0 = DateTime.UtcNow;
            byte[] frame = new byte[0]; int frameLen = 0;
            while (true)
            {
                try
                {
                    if (!Wanted || MacNum == 0)
                    {
                        CloseTx(Wanted ? "keine Adresse vom ESP32" : "");
                        State = "off";
                        _signal.WaitOne(1000);
                        continue;
                    }
                    if (_tx == null || !_tx.Connected)
                    {
                        if (_tx != null) { CloseTx("Verbindung verloren"); State = "failed"; nextTry = DateTime.UtcNow.AddSeconds(3); }
                        if (DateTime.UtcNow < nextTry) { _signal.WaitOne(500); continue; }
                        State = "connecting";
                        string mac = MacString(MacNum);
                        Ambilight.L("bluetooth: verbinde mit " + mac + " ...");
                        var sw = Stopwatch.StartNew();
                        var tx = CreateTransport();
                        string err;
                        if (!tx.Connect(mac, out err))
                        {
                            try { tx.Close(); } catch { }
                            Err = err ?? "unbekannt"; State = "failed"; Fails++;
                            Ambilight.L("bluetooth: Verbindung fehlgeschlagen (" + Err + "), nächster Versuch in 15 s");
                            nextTry = DateTime.UtcNow.AddSeconds(15);
                            continue;
                        }
                        _tx = tx; Mtu = tx.Mtu; Connects++; Err = ""; failRun = 0;
                        State = "connected"; _lastOkUtc = DateTime.UtcNow;
                        Ambilight.L("bluetooth: verbunden in " + sw.ElapsedMilliseconds + " ms, MTU " + Mtu);
                    }

                    if (!_signal.WaitOne(1000)) continue;
                    lock (_lock)
                    {
                        if (_pendingLen <= 0) continue;
                        if (frame.Length < _pendingLen) frame = new byte[_pendingLen];
                        Buffer.BlockCopy(_pending, 0, frame, 0, _pendingLen);
                        frameLen = _pendingLen; _pendingLen = 0;
                    }
                    var fsw = Stopwatch.StartNew();
                    bool ok = true;
                    foreach (var chunk in Chunks(frame, frameLen, Math.Max(20, Mtu - 3)))
                    {
                        string werr;
                        if (!_tx.Write(chunk, out werr)) { ok = false; Err = werr ?? "Schreiben fehlgeschlagen"; break; }
                        Writes++;
                    }
                    LastFrameMs = fsw.Elapsed.TotalMilliseconds;
                    if (ok)
                    {
                        Frames++; fpsFrames++; failRun = 0; _lastOkUtc = DateTime.UtcNow;
                        double s = (DateTime.UtcNow - fpsT0).TotalSeconds;
                        if (s >= 2) { Fps = fpsFrames / s; fpsFrames = 0; fpsT0 = DateTime.UtcNow; }
                    }
                    else if (++failRun >= 5)
                    {
                        Fails++;
                        Ambilight.L("bluetooth: Schreiben klappt nicht (" + Err + "), trenne – Farben per WLAN");
                        CloseTx(Err); State = "failed"; nextTry = DateTime.UtcNow.AddSeconds(5);
                    }
                }
                catch (Exception e)
                {
                    Err = e.GetType().Name + ": " + e.Message;
                    CloseTx(Err); State = "failed"; nextTry = DateTime.UtcNow.AddSeconds(10);
                    Ambilight.L("bluetooth: Fehler " + Err);
                    Thread.Sleep(500);
                }
            }
        }

        static void CloseTx(string why)
        {
            if (_tx == null) return;
            try { _tx.Close(); } catch { }
            _tx = null;
            if (!string.IsNullOrEmpty(why)) Err = why;
        }
    }
}

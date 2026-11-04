using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TizenAmbilight
{
    static partial class Ambilight
    {
        /// <summary>
        /// AmbiSync: kleines Statuspaket (JSON, &lt; 1 KB) alle 2 s per UDP an den ESP32, damit seine
        /// Weboberfläche zeigen kann, was auf dem Fernseher passiert. Enthält NUR Zahlen und kurze
        /// Statustexte - keine Bilder, keine Logs, keine Gerätekennungen. Ziel ist ausschließlich die
        /// konfigurierte LED-Controller-IP.
        /// </summary>
        static class StatusUdp
        {
            public const int DefaultPort = 21325;
            const int IntervalMs = 2000;

            static UdpClient _udp;
            static DateTime _last = DateTime.MinValue;
            static long _lastFrames;
            static DateTime _lastFramesUtc = DateTime.UtcNow;

            public static void Maybe(Config cfg, Stats st)
            {
                if (!cfg.HasWled || cfg.StatusPort <= 0) return;
                DateTime now = DateTime.UtcNow;
                if ((now - _last).TotalMilliseconds < IntervalMs) return;
                _last = now;
                try
                {
                    IPAddress ip;
                    if (!IPAddress.TryParse(cfg.WledIp, out ip)) return;
                    double secs = (now - _lastFramesUtc).TotalSeconds;
                    double fps = secs > 0 ? (st.Frames - _lastFrames) / secs : 0;
                    _lastFrames = st.Frames; _lastFramesUtc = now;

                    var sb = new StringBuilder(512);
                    sb.Append('{');
                    Str(sb, "app", "AmbiSync"); sb.Append(',');
                    Str(sb, "ver", Version); sb.Append(',');
                    Str(sb, "power", st.PowerState); sb.Append(',');
                    Str(sb, "backend", st.BackendActive); sb.Append(',');
                    Str(sb, "status", Trunc(st.LastStatus, 90)); sb.Append(',');
                    Num(sb, "fps", fps); sb.Append(',');
                    Num(sb, "frames", st.Frames); sb.Append(',');
                    Num(sb, "uiOnly", st.UiOnlyFrames); sb.Append(',');
                    Num(sb, "uiBlend", st.UiBlendFrames); sb.Append(',');
                    Num(sb, "sendOk", st.SendOk); sb.Append(',');
                    Num(sb, "sendFail", st.SendFail); sb.Append(',');
                    Num(sb, "leds", cfg.LedCount); sb.Append(',');
                    Num(sb, "zones", cfg.ZoneCount); sb.Append(',');
                    Num(sb, "uptime", (now - st.RunStart).TotalSeconds); sb.Append(',');
                    Str(sb, "bt", BtProbe.Result); sb.Append(',');
                    sb.Append("\"ble\":{");
                    Str(sb, "state", BleLink.Wanted ? BleLink.State : "off"); sb.Append(',');
                    Str(sb, "err", Trunc(BleLink.Err, 80)); sb.Append(',');
                    Num(sb, "mtu", BleLink.Mtu); sb.Append(',');
                    Num(sb, "fps", BleLink.Active ? BleLink.Fps : 0); sb.Append(',');
                    Num(sb, "ms", Math.Round(BleLink.LastFrameMs, 1)); sb.Append(',');
                    Num(sb, "frames", BleLink.Frames); sb.Append(',');
                    Num(sb, "fails", BleLink.Fails); sb.Append("},");
                    sb.Append("\"ppi\":{");
                    Num(sb, "slots", PpiCapture.Slots); sb.Append(',');
                    Num(sb, "pz", PpiCapture.PzRequested); sb.Append(',');
                    Num(sb, "settle", PpiCapture.EffectiveSleepMs); sb.Append(',');
                    Num(sb, "ringMs", Math.Round(PpiCapture.RingMs)); sb.Append("},");
                    sb.Append("\"ui\":{");
                    Str(sb, "mode", UiOverlay.ModeName(UiOverlay.Mode)); sb.Append(',');
                    sb.Append("\"running\":").Append(UiOverlay.Running ? "true" : "false").Append(',');
                    sb.Append("\"alphaSeen\":").Append(UiOverlay.AlphaSeen ? "true" : "false").Append(',');
                    Num(sb, "frames", UiOverlay.Frames); sb.Append(',');
                    Num(sb, "fails", UiOverlay.Fails); sb.Append(',');
                    Num(sb, "w", UiOverlay.LastW); sb.Append(',');
                    Num(sb, "h", UiOverlay.LastH); sb.Append(',');
                    Num(sb, "coverage", UiOverlay.LastCoverage); sb.Append(',');
                    Num(sb, "applied", UiOverlay.AppliedCoverage); sb.Append(',');
                    Num(sb, "transparent", UiOverlay.LastTransparent); sb.Append(',');
                    Num(sb, "nonBlack", UiOverlay.LastNonBlack); sb.Append(',');
                    Num(sb, "capMs", UiOverlay.LastCaptureMs); sb.Append(',');
                    Num(sb, "decMs", UiOverlay.LastDecodeMs); sb.Append(',');
                    Str(sb, "err", Trunc(UiOverlay.LastError, 80));
                    sb.Append("}}");

                    byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
                    if (_udp == null) _udp = new UdpClient();
                    _udp.Send(data, data.Length, new IPEndPoint(ip, cfg.StatusPort));
                }
                catch { /* Status ist optional und darf die Schleife nie stören */ }
            }

            static string Trunc(string s, int n)
            {
                if (string.IsNullOrEmpty(s)) return "";
                return s.Length <= n ? s : s.Substring(0, n);
            }

            static void Str(StringBuilder sb, string k, string v)
            {
                sb.Append('"').Append(k).Append("\":\"");
                foreach (char ch in v ?? "")
                {
                    if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                    else if (ch < 0x20) sb.Append(' ');
                    else sb.Append(ch);
                }
                sb.Append('"');
            }

            static void Num(StringBuilder sb, string k, double v)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
                sb.Append('"').Append(k).Append("\":").Append(v.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
    }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TizenAmbilight
{
    static partial class Ambilight
    {
        /// <summary>
        /// AmbiSync: den ESP32 im Heimnetz automatisch finden.
        ///
        /// Steht in config.json "wledIp": "auto" (Standard), schickt die App einen UDP-Broadcast
        /// "AMBISYNC-DISCOVER" an Port 21326. Der ESP32 antwortet von genau diesem Port mit
        /// "AMBISYNC 1 leds=&lt;anzahl&gt; port=21324". Daraus werden Ziel-IP, LED-Anzahl und die
        /// Steuer-URL (http://&lt;ip&gt;/tv/control) übernommen. Bis ein ESP32 antwortet, wird alle
        /// 3 s erneut gefragt (der Fernseher startet oft schneller als das WLAN).
        ///
        /// Antwortet der ESP32 später nicht mehr (z. B. neue IP nach einem Router-Neustart), sucht
        /// <see cref="NoteControlPoll"/> im Hintergrund erneut.
        ///
        /// Wer das nicht möchte, trägt in config.json eine feste IP ein - dann wird nie gesucht.
        /// </summary>
        const int DiscoveryPort = 21326;
        static bool _autoDiscovered;
        static bool _controlFromDiscovery;
        static int _ctrlFails;
        static int _rediscovering;

        static void AutoDiscover(Config cfg, bool ledCountFixed, bool zoneCountFixed)
        {
            if (!string.Equals(cfg.WledIp, "auto", StringComparison.OrdinalIgnoreCase)) return;
            L("discovery: suche AmbiSync-ESP32 per Broadcast auf Port " + DiscoveryPort + " ...");
            int attempt = 0;
            while (!_stopping)
            {
                attempt++;
                string ip; int leds, port;
                if (DiscoverOnce(out ip, out leds, out port))
                {
                    cfg.WledIp = ip;
                    if (port > 0 && port <= 65535) cfg.WledPort = port;
                    if (leds >= 4 && leds <= WledOutput.MaxDrgbLeds)
                    {
                        if (!ledCountFixed) cfg.LedCount = leds;
                        if (!zoneCountFixed && !ledCountFixed) cfg.ZoneCount = leds;
                    }
                    if (string.IsNullOrEmpty(cfg.ControlUrl))
                    {
                        cfg.ControlUrl = "http://" + ip + "/tv/control";
                        _controlFromDiscovery = true;
                    }
                    _autoDiscovered = true;
                    L("discovery: ESP32 gefunden: " + cfg.WledIp + ":" + cfg.WledPort + ", " + cfg.LedCount +
                      " LEDs, Steuerung " + cfg.ControlUrl + " (Versuch " + attempt + ")");
                    return;
                }
                if (attempt == 1 || attempt % 20 == 0) L("discovery: noch kein ESP32 gefunden, neuer Versuch in 3 s");
                Thread.Sleep(3000);
            }
        }

        /// <summary>Vom I/O-Thread nach jeder Abfrage der Steuer-URL aufgerufen. Nach ~10 s ohne
        /// Antwort wird im Hintergrund neu gesucht (nur IP/Port/Steuer-URL, die Zonenzahl bleibt).</summary>
        static void NoteControlPoll(Config cfg, bool ok)
        {
            if (ok) { _ctrlFails = 0; return; }
            if (!_autoDiscovered || ++_ctrlFails < 40) return;
            _ctrlFails = 0;
            if (Interlocked.Exchange(ref _rediscovering, 1) == 1) return;
            var t = new Thread(() =>
            {
                try
                {
                    string ip; int leds, port;
                    if (DiscoverOnce(out ip, out leds, out port) && ip != cfg.WledIp)
                    {
                        L("discovery: ESP32 hat jetzt die IP " + ip + " (vorher " + cfg.WledIp + ")");
                        if (port > 0 && port <= 65535) cfg.WledPort = port;
                        if (_controlFromDiscovery) cfg.ControlUrl = "http://" + ip + "/tv/control";
                        cfg.WledIp = ip;
                    }
                }
                catch (Exception e) { L("rediscovery: " + e.GetType().Name + ": " + e.Message); }
                finally { Interlocked.Exchange(ref _rediscovering, 0); }
            }) { IsBackground = true, Name = "rediscover" };
            t.Start();
        }

        /// <summary>Ein Broadcast, 1,5 s auf eine gültige Antwort warten.</summary>
        static bool DiscoverOnce(out string ip, out int leds, out int port)
        {
            ip = null; leds = 0; port = 0;
            try
            {
                using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                {
                    udp.EnableBroadcast = true;
                    udp.Client.ReceiveTimeout = 1500;
                    byte[] q = Encoding.ASCII.GetBytes("AMBISYNC-DISCOVER");
                    udp.Send(q, q.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                    DateTime until = DateTime.UtcNow.AddMilliseconds(1500);
                    while (DateTime.UtcNow < until)
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] r;
                        try { r = udp.Receive(ref from); }
                        catch (SocketException) { break; }   // Zeit abgelaufen
                        if (from.Port != DiscoveryPort) continue;   // der ESP32 antwortet vom Suchport
                        string s = Encoding.ASCII.GetString(r, 0, Math.Min(r.Length, 200));
                        if (!ParseDiscoveryReply(s, out leds, out port)) continue;
                        ip = from.Address.ToString();
                        return true;
                    }
                }
            }
            catch (Exception e) { L("discovery: " + e.GetType().Name + ": " + e.Message); }
            return false;
        }

        /// <summary>"AMBISYNC 1 leds=260 port=21324" -> leds, port. Alles andere wird ignoriert.</summary>
        internal static bool ParseDiscoveryReply(string s, out int leds, out int port)
        {
            leds = 0; port = 0;
            if (s == null || !s.StartsWith("AMBISYNC ", StringComparison.Ordinal)) return false;
            foreach (string part in s.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                int v;
                if (!int.TryParse(part.Substring(eq + 1), out v)) continue;
                string k = part.Substring(0, eq);
                if (k == "leds") leds = v;
                else if (k == "port") port = v;
            }
            return true;
        }
    }
}

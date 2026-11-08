using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Tizen.Applications;

namespace TizenAmbilight
{
    static partial class Ambilight
    {
        /// <summary>
        /// AmbiSync: den ESP32 im Heimnetz automatisch finden (Plug &amp; Play).
        ///
        /// Steht in config.json "wledIp": "auto" (Standard), gilt diese Reihenfolge:
        ///   1. Die zuletzt gefundene Adresse (gemerkt in esp32.cache im App-Datenordner) wird direkt
        ///      gefragt. Nach einem TV-Neustart ist der ESP32 so in Bruchteilen einer Sekunde wieder da.
        ///   2. Ein Broadcast "AMBISYNC-DISCOVER" an Port 21326, und zwar an 255.255.255.255 UND an die
        ///      Broadcast-Adresse jedes eigenen Netzes (manche Router reichen 255.255.255.255 nicht vom
        ///      LAN-Kabel ins WLAN weiter). Der ESP32 antwortet von Port 21326 mit
        ///      "AMBISYNC 1 leds=&lt;anzahl&gt; port=21324".
        ///   3. Parallel hört die App auf Port 21327 mit: Der ESP32 (ab Firmware 1.1.0) meldet sich dort
        ///      selbst alle 2 s, solange kein Fernseher verbunden ist.
        /// Bis ein ESP32 gefunden ist, wird alle 2 s erneut gefragt (der Fernseher startet oft schneller
        /// als das WLAN).
        ///
        /// Antwortet der ESP32 später nicht mehr (z. B. neue IP nach einem Router-Neustart), sucht
        /// <see cref="NoteControlPoll"/> nach ~3 s im Hintergrund neu, und eine Meldung auf Port 21327
        /// von einer neuen Adresse wird sofort übernommen.
        ///
        /// Wer das nicht möchte, trägt in config.json eine feste IP ein - dann wird nie gesucht.
        /// </summary>
        const int DiscoveryPort = 21326;
        const int BeaconPort = 21327;
        static bool _autoDiscovered;
        static bool _controlFromDiscovery;
        static bool _ledCountFixed, _zoneCountFixed;
        static int _ctrlFails;
        static int _rediscovering;

        // zuletzt per Selbstmeldung (Port 21327) gehörter ESP32
        static string _beaconIp;
        static int _beaconLeds, _beaconPort;
        static DateTime _beaconUtc = DateTime.MinValue;
        static readonly object _beaconLock = new object();
        static Thread _beaconThread;

        static void AutoDiscover(Config cfg, bool ledCountFixed, bool zoneCountFixed, bool background = false)
        {
            _ledCountFixed = ledCountFixed;
            _zoneCountFixed = zoneCountFixed;
            if (!string.Equals(cfg.WledIp, "auto", StringComparison.OrdinalIgnoreCase)) return;
            StartBeaconListener();
            string cached = ReadCache();
            L("discovery: suche AmbiSync-ESP32" + (cached != null ? " (zuletzt " + cached + ")" : "") +
              " per Broadcast auf Port " + DiscoveryPort + " und Selbstmeldung auf Port " + BeaconPort + " ...");
            int attempt = 0;
            while (!_stopping)
            {
                attempt++;
                string ip; int leds, port; string how;
                if (FindEsp(cached, out ip, out leds, out port, out how))
                {
                    cfg.WledIp = ip;
                    if (port > 0 && port <= 65535) cfg.WledPort = port;
                    // im Hintergrund läuft die Hauptschleife schon: LED-Anzahl dann nur über den Steuerkanal (leds=)
                    if (!background && leds >= 4 && leds <= WledOutput.MaxDrgbLeds)
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
                    WriteCache(ip);
                    L("discovery: ESP32 gefunden (" + how + "): " + cfg.WledIp + ":" + cfg.WledPort + ", " + cfg.LedCount +
                      " LEDs, Steuerung " + cfg.ControlUrl + " (Versuch " + attempt + ")");
                    return;
                }
                if (attempt == 1 || attempt % 30 == 0) L("discovery: noch kein ESP32 gefunden, neuer Versuch in 2 s");
                // in der Pause weiter auf Selbstmeldungen achten
                for (int i = 0; i < 20 && !_stopping; i++)
                {
                    if (BeaconFresh(5000)) break;
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>Ein Suchdurchgang: frische Selbstmeldung, gemerkte Adresse, dann Broadcast.</summary>
        static bool FindEsp(string cached, out string ip, out int leds, out int port, out string how)
        {
            ip = null; leds = 0; port = 0; how = "";
            if (TakeBeacon(5000, out ip, out leds, out port)) { how = "Selbstmeldung"; return true; }
            if (cached != null && DiscoverOnce(new[] { cached }, 700, out ip, out leds, out port)) { how = "gemerkte Adresse"; return true; }
            if (DiscoverOnce(BroadcastTargets(), 1500, out ip, out leds, out port)) { how = "Broadcast"; return true; }
            if (TakeBeacon(5000, out ip, out leds, out port)) { how = "Selbstmeldung"; return true; }
            return false;
        }

        /// <summary>Vom I/O-Thread nach jeder Abfrage der Steuer-URL aufgerufen (alle 250 ms). Nach ~3 s
        /// ohne Antwort wird im Hintergrund neu gesucht (nur IP/Port/Steuer-URL, die Zonenzahl bleibt).</summary>
        static void NoteControlPoll(Config cfg, bool ok)
        {
            if (ok) { _ctrlFails = 0; return; }
            if (!_autoDiscovered) return;
            // Meldet sich der ESP32 gerade von einer anderen Adresse, sofort umziehen
            string bip; int bl, bp;
            if (TakeBeacon(10000, out bip, out bl, out bp) && bip != cfg.WledIp)
            {
                SwitchEsp(cfg, bip, bp, "Selbstmeldung");
                _ctrlFails = 0;
                return;
            }
            if (++_ctrlFails < 12) return;
            _ctrlFails = 0;
            if (Interlocked.Exchange(ref _rediscovering, 1) == 1) return;
            var t = new Thread(() =>
            {
                try
                {
                    string ip; int leds, port;
                    if (DiscoverOnce(BroadcastTargets(), 1500, out ip, out leds, out port) && ip != cfg.WledIp)
                        SwitchEsp(cfg, ip, port, "Broadcast");
                }
                catch (Exception e) { L("rediscovery: " + e.GetType().Name + ": " + e.Message); }
                finally { Interlocked.Exchange(ref _rediscovering, 0); }
            }) { IsBackground = true, Name = "rediscover" };
            t.Start();
        }

        static void SwitchEsp(Config cfg, string ip, int port, string how)
        {
            L("discovery: ESP32 hat jetzt die IP " + ip + " (vorher " + cfg.WledIp + ", " + how + ")");
            if (port > 0 && port <= 65535) cfg.WledPort = port;
            if (_controlFromDiscovery) cfg.ControlUrl = "http://" + ip + "/tv/control";
            cfg.WledIp = ip;
            WriteCache(ip);
        }

        /// <summary>Fragt die angegebenen Ziele und wartet bis zu waitMs auf eine gültige Antwort.</summary>
        static bool DiscoverOnce(IList<IPAddress> targets, int waitMs, out string ip, out int leds, out int port)
        {
            ip = null; leds = 0; port = 0;
            if (targets == null || targets.Count == 0) return false;
            try
            {
                using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                {
                    udp.EnableBroadcast = true;
                    udp.Client.ReceiveTimeout = 250;
                    byte[] q = Encoding.ASCII.GetBytes("AMBISYNC-DISCOVER");
                    foreach (IPAddress t in targets)
                    {
                        try { udp.Send(q, q.Length, new IPEndPoint(t, DiscoveryPort)); }
                        catch (SocketException) { /* ein Netz ohne Route: die übrigen trotzdem fragen */ }
                    }
                    DateTime until = DateTime.UtcNow.AddMilliseconds(waitMs);
                    while (DateTime.UtcNow < until)
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] r;
                        try { r = udp.Receive(ref from); }
                        catch (SocketException) { if (TakeBeacon(3000, out ip, out leds, out port)) return true; continue; }
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

        static bool DiscoverOnce(string[] hosts, int waitMs, out string ip, out int leds, out int port)
        {
            var list = new List<IPAddress>();
            foreach (string h in hosts) { IPAddress a; if (IPAddress.TryParse(h, out a)) list.Add(a); }
            return DiscoverOnce(list, waitMs, out ip, out leds, out port);
        }

        /// <summary>255.255.255.255 plus die Broadcast-Adresse jedes eigenen IPv4-Netzes.</summary>
        internal static List<IPAddress> BroadcastTargets()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        IPAddress bc = SubnetBroadcast(ua.Address, ua.IPv4Mask);
                        if (bc != null && !list.Contains(bc)) list.Add(bc);
                    }
                }
            }
            catch (Exception e) { L("discovery: Netzwerkliste nicht lesbar (" + e.GetType().Name + "), nur 255.255.255.255"); }
            return list;
        }

        internal static IPAddress SubnetBroadcast(IPAddress ip, IPAddress mask)
        {
            byte[] a = ip.GetAddressBytes(), m = mask.GetAddressBytes();
            if (a.Length != 4 || m.Length != 4) return null;
            if (m[0] == 0 && m[1] == 0 && m[2] == 0 && m[3] == 0) return null;
            if (m[0] == 255 && m[1] == 255 && m[2] == 255 && m[3] == 255) return null;   // /32: kein Netz
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            return new IPAddress(b);
        }

        // ---------------------------------------------------------------- Selbstmeldung (Port 21327)

        static void StartBeaconListener()
        {
            if (_beaconThread != null) return;
            _beaconThread = new Thread(BeaconLoop) { IsBackground = true, Name = "esp-beacon" };
            _beaconThread.Start();
        }

        static void BeaconLoop()
        {
            UdpClient udp = null;
            int failures = 0;
            while (!_stopping)
            {
                try
                {
                    if (udp == null)
                    {
                        udp = new UdpClient();
                        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
                        udp.Client.ReceiveTimeout = 1000;
                        failures = 0;
                    }
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] r;
                    try { r = udp.Receive(ref from); }
                    catch (SocketException) { continue; }   // Zeit abgelaufen
                    string s = Encoding.ASCII.GetString(r, 0, Math.Min(r.Length, 200));
                    int leds, port;
                    if (!ParseDiscoveryReply(s, out leds, out port)) continue;
                    lock (_beaconLock)
                    {
                        bool isNew = _beaconIp != from.Address.ToString();
                        _beaconIp = from.Address.ToString();
                        _beaconLeds = leds;
                        _beaconPort = port;
                        _beaconUtc = DateTime.UtcNow;
                        if (isNew) L("discovery: Selbstmeldung von " + _beaconIp + " (" + leds + " LEDs)");
                    }
                }
                catch (Exception e)
                {
                    try { if (udp != null) udp.Close(); } catch { }
                    udp = null;
                    if (++failures == 1 || failures % 60 == 0) L("discovery: Port " + BeaconPort + " nicht nutzbar (" + e.GetType().Name + ": " + e.Message + ")");
                    Thread.Sleep(Math.Min(30000, 1000 * failures));
                }
            }
            try { if (udp != null) udp.Close(); } catch { }
        }

        static bool BeaconFresh(int maxAgeMs)
        {
            lock (_beaconLock) return _beaconIp != null && (DateTime.UtcNow - _beaconUtc).TotalMilliseconds <= maxAgeMs;
        }

        static bool TakeBeacon(int maxAgeMs, out string ip, out int leds, out int port)
        {
            lock (_beaconLock)
            {
                ip = _beaconIp; leds = _beaconLeds; port = _beaconPort;
                return ip != null && (DateTime.UtcNow - _beaconUtc).TotalMilliseconds <= maxAgeMs;
            }
        }

        // ---------------------------------------------------------------- gemerkte Steuerung (Bluetooth)

        static string _ctrlCacheWritten;

        static string ControlCachePath()
        {
            string p = CachePath();
            return p == null ? null : Path.Combine(Path.GetDirectoryName(p), "control.cache");
        }

        /// <summary>true, wenn der Steuertext das Token k=v enthält (z. B. "ble=1").</summary>
        internal static bool HasToken(string body, string k, string v)
        {
            foreach (string t in body.Split(new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(t.Trim(), k + "=" + v, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Letzten Steuertext vom ESP32 merken (ohne "pause"), damit die App beim nächsten Start auch
        /// ohne WLAN-Verbindung zum ESP32 weiß, dass sie Bluetooth nehmen soll und mit wie vielen LEDs.</summary>
        static void WriteControlCache(string body)
        {
            try
            {
                var keep = new List<string>();
                foreach (string t in body.Split(new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string x = t.Trim();
                    if (x.Length == 0 || string.Equals(x, "pause", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "run", StringComparison.OrdinalIgnoreCase)) continue;
                    keep.Add(x);
                }
                string s = string.Join(",", keep.ToArray());
                if (s == _ctrlCacheWritten) return;
                string p = ControlCachePath();
                if (p == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, s);
                _ctrlCacheWritten = s;
            }
            catch { }
        }

        static string ReadControlCache()
        {
            try
            {
                string p = ControlCachePath();
                if (p == null || !File.Exists(p)) return null;
                string s = File.ReadAllText(p).Trim();
                _ctrlCacheWritten = s;
                return s.Length > 0 && s.Length < 4000 ? s : null;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- gemerkte Adresse

        static string CachePath()
        {
            try
            {
                string d = Application.Current?.DirectoryInfo?.Data;
                if (!string.IsNullOrEmpty(d)) return Path.Combine(d, "esp32.cache");
            }
            catch { }
            return null;
        }

        static string ReadCache()
        {
            try
            {
                string p = CachePath();
                if (p == null || !File.Exists(p)) return null;
                string s = File.ReadAllText(p).Trim();
                IPAddress a;
                return IPAddress.TryParse(s, out a) && a.AddressFamily == AddressFamily.InterNetwork ? s : null;
            }
            catch { return null; }
        }

        static void WriteCache(string ip)
        {
            try
            {
                string p = CachePath();
                if (p == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, ip);
            }
            catch (Exception e) { L("discovery: Adresse nicht gemerkt (" + e.GetType().Name + ")"); }
        }

        /// <summary>"AMBISYNC 1 leds=260 port=21324 fw=1.1.0" -> leds, port. Alles andere wird ignoriert.</summary>
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

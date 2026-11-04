using System;
using System.Threading;
using System.Threading.Tasks;
using Tizen.Network.Bluetooth;

namespace TizenAmbilight
{
    static partial class BleLink
    {
        static IBleTransport CreateTransport() { return new TizenBleTransport(); }
    }

    /// <summary>
    /// Bluetooth LE über TizenFX (Tizen.Network.Bluetooth, GATT-Client). Nur im TV-Build (die PC-Tests haben eine
    /// Attrappe). Rückrufe des Bluetooth-Stacks laufen über die Hauptschleife der Dienst-App; hier wird nur
    /// gewartet, nie die Hauptschleife blockiert.
    /// </summary>
    sealed class TizenBleTransport : IBleTransport
    {
        // muss zur ESP32-Firmware passen (BLE_SVC_UUID / BLE_CHR_UUID)
        const string SvcUuid = "a3b5f001-7a6d-4b1e-9c2f-616d62690001";
        const string ChrUuid = "a3b5f002-7a6d-4b1e-9c2f-616d62690001";

        BluetoothGattClient _client;
        BluetoothGattCharacteristic _chr;
        volatile bool _connected;
        int _mtu = 23;
        string _mac;

        public int Mtu { get { return _mtu; } }
        public bool Connected { get { return _connected; } }

        public bool Connect(string mac, out string err)
        {
            err = null;
            _mac = mac;
            try
            {
                if (!BluetoothAdapter.IsBluetoothEnabled) { err = "Bluetooth am Fernseher ist aus"; return false; }
            }
            catch (NotSupportedException) { err = "Fernseher bietet Apps kein Bluetooth an"; return false; }
            catch (Exception e) { err = "Bluetooth nicht nutzbar: " + e.Message; return false; }

            // Erst kurz suchen: manche Bluetooth-Stacks verbinden nur mit Geräten, die sie gerade gesehen haben.
            string scanNote = Scan(mac, 8000);
            Ambilight.L("bluetooth: Suche " + scanNote);

            try { _client = BluetoothGattClient.CreateClient(mac); }
            catch (Exception e) { err = "GATT-Client: " + e.Message; return false; }
            if (_client == null) { err = "GATT-Client konnte nicht angelegt werden"; return false; }
            _client.ConnectionStateChanged += OnState;

            try
            {
                Task t = _client.ConnectAsync(false);
                if (!t.Wait(15000)) { err = "Verbindung: keine Antwort in 15 s (" + scanNote + ")"; return false; }
            }
            catch (Exception e) { err = "Verbindung: " + Inner(e) + " (" + scanNote + ")"; return false; }
            _connected = true;

            BluetoothGattService svc = null;
            for (int i = 0; i < 40 && svc == null; i++)
            {
                try { svc = _client.GetService(SvcUuid); } catch { }
                if (svc == null) Thread.Sleep(250);
            }
            if (svc == null) { err = "AmbiSync-Dienst nicht gefunden (Firmware 1.5?)"; return false; }
            try { _chr = svc.GetCharacteristic(ChrUuid); } catch (Exception e) { err = "Eigenschaft: " + e.Message; return false; }
            if (_chr == null) { err = "Eigenschaft nicht gefunden"; return false; }
            try { _chr.WriteType = BluetoothGattWriteType.NoResponse; } catch { }

            // Größere Pakete: 517 statt 23 Byte -> ein Bild in 2 statt ~40 Schreibvorgängen
            try { _client.SetAttMtu(517); } catch (Exception e) { Ambilight.L("bluetooth: MTU-Anfrage: " + e.Message); }
            for (int i = 0; i < 20; i++)
            {
                try { _mtu = _client.GetAttMtu(); } catch { }
                if (_mtu > 23) break;
                Thread.Sleep(100);
            }
            if (_mtu < 23) _mtu = 23;
            return true;
        }

        string Scan(string mac, int ms)
        {
            var found = new ManualResetEventSlim(false);
            int seen = 0;
            EventHandler<AdapterLeScanResultChangedEventArgs> h = (s, e) =>
            {
                try
                {
                    seen++;
                    string a = e.DeviceData != null ? e.DeviceData.RemoteAddress : null;
                    if (a != null && string.Equals(a, mac, StringComparison.OrdinalIgnoreCase)) found.Set();
                }
                catch { }
            };
            try
            {
                BluetoothAdapter.ScanResultChanged += h;
                BluetoothAdapter.StartLeScan();
            }
            catch (Exception e)
            {
                try { BluetoothAdapter.ScanResultChanged -= h; } catch { }
                return "nicht möglich (" + e.Message + ")";
            }
            bool ok = found.Wait(ms);
            try { BluetoothAdapter.StopLeScan(); } catch { }
            try { BluetoothAdapter.ScanResultChanged -= h; } catch { }
            return ok ? "ESP32 gefunden" : ("ESP32 nicht gesehen (" + seen + " andere Geräte)");
        }

        void OnState(object s, GattConnectionStateChangedEventArgs e)
        {
            try
            {
                if (!e.IsConnected) { _connected = false; Ambilight.L("bluetooth: getrennt (Code " + e.Result + ")"); }
            }
            catch { }
        }

        public bool Write(byte[] data, out string err)
        {
            err = null;
            if (!_connected || _client == null || _chr == null) { err = "nicht verbunden"; return false; }
            try
            {
                _chr.Value = data;
                Task<bool> t = _client.WriteValueAsync(_chr);
                if (!t.Wait(1000)) { err = "Schreiben: keine Antwort"; return false; }
                if (!t.Result) { err = "Schreiben abgelehnt"; return false; }
                return true;
            }
            catch (Exception e) { err = "Schreiben: " + Inner(e); return false; }
        }

        public void Close()
        {
            _connected = false;
            var c = _client; _client = null; _chr = null;
            if (c == null) return;
            try { c.ConnectionStateChanged -= OnState; } catch { }
            try { c.DisconnectAsync().Wait(3000); } catch { }
            try { c.Dispose(); } catch { }
        }

        static string Inner(Exception e)
        {
            var ae = e as AggregateException;
            if (ae != null && ae.InnerException != null) e = ae.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }
    }
}

/*
  AmbiSync – ESP32-Firmware für das Fernseher-Ambilight

  Empfängt Farben vom Fernseher (AmbiSync-TV-App) und steuert einen WS2812B/SK6812-Streifen.

  * UDP 21324: WLED-Echtzeitprotokoll (WARLS, DRGB, DRGBW, DNRGB) – das sendet die TV-App,
               aber auch HyperHDR/Hyperion können es.
  * UDP 4048:  DDP (optional, z. B. HyperHDR).
  * UDP 21325: Statuspaket der TV-App (nur Zahlen, wird in der Weboberfläche angezeigt).
  * HTTP 80:   Weboberfläche, Einstellungen, Testbilder, Firmware-Update.
               /tv/control liefert der TV-App ihre Einstellungen (Helligkeit, Mapping, Menümodus ...).

  Erster Start: Der ESP32 öffnet das WLAN "AmbiSync-Setup" (Passwort: ambisync-setup).
  Verbinden, http://192.168.4.1 öffnen, Heim-WLAN eintragen.

  Benötigt: Arduino-ESP32-Core 3.x. Keine weiteren Bibliotheken.
*/

#include <WiFi.h>
#include <WebServer.h>
#include <DNSServer.h>
#include <ESPmDNS.h>
#include <AsyncUDP.h>
#include <Preferences.h>
#include <Update.h>
#include "esp32-hal-rmt.h"
#include <atomic>
#include "webui.h"

// Vorwärtsdeklarationen (Funktionen werden vor ihrer Definition benutzt)
bool pinSafe(int p);
int defaultPin();

// ---- Protokoll im seriellen Monitor (115200 Baud): alles, was der ESP32 tut ----
// Bei ESP32-C3/-S3 mit "USB CDC On Boot: Disabled" landet Serial auf den UART-Pins statt am USB-Kabel.
// Dann zusätzlich über den eingebauten USB-Port ausgeben, damit der Monitor trotzdem etwas zeigt.
#if defined(SOC_USB_SERIAL_JTAG_SUPPORTED) && SOC_USB_SERIAL_JTAG_SUPPORTED && ARDUINO_USB_MODE && !ARDUINO_USB_CDC_ON_BOOT
#define AMB_USB_MIRROR 1
HWCDC usbLog;
#endif

static const int LOG_LINES = 60, LOG_W = 200;
static char logRing[LOG_LINES][LOG_W];
static int logHead = 0;
static uint32_t logSeq = 0;
static portMUX_TYPE logMux = portMUX_INITIALIZER_UNLOCKED;

void outRaw(const char *s) {
  Serial.println(s);
#ifdef AMB_USB_MIRROR
  usbLog.println(s);
#endif
}
void logLine(const char *s) {
  outRaw(s);
  portENTER_CRITICAL(&logMux);
  strlcpy(logRing[logHead], s, LOG_W);
  logHead = (logHead + 1) % LOG_LINES;
  logSeq++;
  portEXIT_CRITICAL(&logMux);
}
void ambLog(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
void ambLog(const char *fmt, ...) {
  char b[320];
  int n = snprintf(b, sizeof(b), "[%8.1f s] ", millis() / 1000.0);
  va_list a; va_start(a, fmt); vsnprintf(b + n, sizeof(b) - n, fmt, a); va_end(a);
  logLine(b);
}
#define LOG(fmt, ...) ambLog(fmt, ##__VA_ARGS__)

// letzte Protokollzeilen als Text (für die Weboberfläche)
String logText() {
  String o; o.reserve(LOG_LINES * 80);
  portENTER_CRITICAL(&logMux);
  int cnt = logSeq < (uint32_t)LOG_LINES ? (int)logSeq : LOG_LINES;
  int start = (logHead - cnt + LOG_LINES) % LOG_LINES;
  static char copy[LOG_LINES][LOG_W];
  for (int i = 0; i < cnt; i++) memcpy(copy[i], logRing[(start + i) % LOG_LINES], LOG_W);
  portEXIT_CRITICAL(&logMux);
  for (int i = 0; i < cnt; i++) { o += copy[i]; o += '\n'; }
  return o;
}

const char *wlStatusName(int s) {
  switch (s) {
    case WL_IDLE_STATUS: return "wartet";
    case WL_NO_SSID_AVAIL: return "Netz nicht gefunden";
    case WL_SCAN_COMPLETED: return "Suche fertig";
    case WL_CONNECTED: return "verbunden";
    case WL_CONNECT_FAILED: return "Verbindung fehlgeschlagen (Passwort?)";
    case WL_CONNECTION_LOST: return "Verbindung verloren";
    case WL_DISCONNECTED: return "nicht verbunden";
    default: return "unbekannt";
  }
}

#define FW_VERSION "1.0.3"

#if defined(CONFIG_IDF_TARGET_ESP32) || defined(CONFIG_IDF_TARGET_ESP32S3)
#define AMB_RMT_MEM RMT_MEM_NUM_BLOCKS_4
#else
#define AMB_RMT_MEM RMT_MEM_NUM_BLOCKS_2
#endif

// ------------------------------------------------------------------ Grenzen
static const int MAX_LEDS = 490;                 // Grenze des DRGB-Protokolls
static const int PORT_REALTIME = 21324;
static const int PORT_DDP = 4048;
static const int PORT_TVSTATUS = 21325;
static const int PORT_DISCOVERY = 21326;   // TV-App sucht den ESP32 per Broadcast
static const char *AP_SSID = "AmbiSync-Setup";
static const char *AP_PASS = "ambisync-setup";

// ------------------------------------------------------------------ Einstellungen
struct Settings {
  String ssid, pass;
  String hostname = "ambisync";
  String adminPass;           // leer = keine Anmeldung nötig
  int ledPin = 16;
  int ledCount = 60;
  int colorOrder = 0;         // 0 GRB, 1 RGB, 2 BRG, 3 RBG, 4 GBR, 5 BGR
  int brightness = 255;       // 0..255
  int maxCurrent = 10000;     // mA, 0 = keine Begrenzung
  int mAPerChannel = 20;      // WS2812B: ~20 mA pro Farbkanal bei voller Helligkeit
  int smoothing = 40;         // 0..95 %: Zwischenbilder weich überblenden
  int idleMode = 0;           // 0 aus, 1 feste Farbe
  uint32_t idleColor = 0x201000;
  int idleBrightness = 64;
  String allowIp;             // leer = Farben von jeder Quelle annehmen
  String tvTokens;            // Einstellungen für die TV-App, z. B. "gain=1.2,ui=1"
} cfg;

Preferences prefs;
static std::atomic<uint32_t> allowIpNum{0};   // 0 = alle; als Zahl, damit UDP-Task und Web-Task sich nicht stören

void applyAllowIp() {
  IPAddress a;
  allowIpNum = (cfg.allowIp.length() && a.fromString(cfg.allowIp)) ? (uint32_t)a : 0;
}

void loadSettings() {
  prefs.begin("ambisync", true);
  cfg.ssid = prefs.getString("ssid", "");
  cfg.pass = prefs.getString("pass", "");
  cfg.hostname = prefs.getString("host", "ambisync");
  cfg.adminPass = prefs.getString("admin", "");
  cfg.ledPin = prefs.getInt("pin", -1);
  if (!pinSafe(cfg.ledPin)) cfg.ledPin = defaultPin();
  cfg.ledCount = constrain(prefs.getInt("leds", 60), 1, MAX_LEDS);
  cfg.colorOrder = constrain(prefs.getInt("order", 0), 0, 5);
  cfg.brightness = constrain(prefs.getInt("bri", 255), 0, 255);
  { int v = prefs.getInt("maxmA", 10000); cfg.maxCurrent = v < 0 ? 0 : v; }
  cfg.mAPerChannel = constrain(prefs.getInt("mAch", 20), 1, 60);
  cfg.smoothing = constrain(prefs.getInt("smooth", 40), 0, 95);
  cfg.idleMode = constrain(prefs.getInt("idle", 0), 0, 1);
  cfg.idleColor = prefs.getUInt("idleCol", 0x201000);
  cfg.idleBrightness = constrain(prefs.getInt("idleBri", 64), 0, 255);
  cfg.allowIp = prefs.getString("allow", "");
  cfg.tvTokens = prefs.getString("tv", "");
  prefs.end();
  applyAllowIp();
}

void saveSettings() {
  prefs.begin("ambisync", false);
  prefs.putString("ssid", cfg.ssid);
  prefs.putString("pass", cfg.pass);
  prefs.putString("host", cfg.hostname);
  prefs.putString("admin", cfg.adminPass);
  prefs.putInt("pin", cfg.ledPin);
  prefs.putInt("leds", cfg.ledCount);
  prefs.putInt("order", cfg.colorOrder);
  prefs.putInt("bri", cfg.brightness);
  prefs.putInt("maxmA", cfg.maxCurrent);
  prefs.putInt("mAch", cfg.mAPerChannel);
  prefs.putInt("smooth", cfg.smoothing);
  prefs.putInt("idle", cfg.idleMode);
  prefs.putUInt("idleCol", cfg.idleColor);
  prefs.putInt("idleBri", cfg.idleBrightness);
  prefs.putString("allow", cfg.allowIp);
  prefs.putString("tv", cfg.tvTokens);
  prefs.end();
}

// Pins, an denen der LED-Ausgang ungefährlich ist (kein Flash/PSRAM/USB/Boot-Strapping-Problem)
bool pinSafe(int p) {
#if defined(CONFIG_IDF_TARGET_ESP32)
  static const int ok[] = {2, 4, 5, 13, 14, 15, 16, 17, 18, 19, 21, 22, 23, 25, 26, 27, 32, 33};
  bool psram = psramFound();
  for (int q : ok) if (q == p) return !(psram && (p == 16 || p == 17));
  return false;
#elif defined(CONFIG_IDF_TARGET_ESP32S3)
  return (p >= 1 && p <= 18) || p == 21 || (p >= 38 && p <= 48);
#elif defined(CONFIG_IDF_TARGET_ESP32C3)
  return (p >= 0 && p <= 7) || p == 10 || p == 20 || p == 21;
#else
  return p >= 0 && p <= 48;
#endif
}

int defaultPin() {
#if defined(CONFIG_IDF_TARGET_ESP32C3)
  return 4;
#else
  return pinSafe(16) ? 16 : 18;
#endif
}

// ------------------------------------------------------------------ Bildpuffer
// target: was zuletzt empfangen wurde. out: was gerade auf dem Streifen ist (weich nachgeführt).
static uint8_t target[MAX_LEDS * 3];
static float outBuf[MAX_LEDS * 3];
static portMUX_TYPE frameMux = portMUX_INITIALIZER_UNLOCKED;

static volatile uint32_t lastRealtimeMs = 0;     // 0 = noch nie
static volatile uint32_t realtimeTimeoutMs = 0;  // 0 = nie ablaufen
static std::atomic<uint32_t> rtPackets{0}, rtRejected{0}, rtFrames{0};
static volatile uint32_t rtSourceIp = 0;          // IPv4 als Zahl (thread-sicher)
static const char *volatile rtProtocol = "-";

// Testbilder (haben Vorrang vor allem anderen, solange aktiv)
enum TestMode { TEST_NONE, TEST_EDGES, TEST_FIRST, TEST_WHITE, TEST_RAINBOW, TEST_OFF };
static volatile int testMode = TEST_NONE;
static volatile uint32_t testUntilMs = 0;

// TV-Status
static char tvStatusBuf[1401];
static volatile uint32_t tvStatusMs = 0;
static volatile uint32_t tvIpNum = 0;
static portMUX_TYPE tvMux = portMUX_INITIALIZER_UNLOCKED;

// Ausgabe-Statistik
static std::atomic<uint32_t> outFrames{0};
static std::atomic<uint32_t> discCount{0}, discIp{0};
static volatile float outFps = 0, rtFps = 0;
static volatile int estCurrent = 0;
static volatile bool limiting = false;

// ------------------------------------------------------------------ LED-Ausgabe (RMT, ohne Bibliothek)
static rmt_data_t *rmtBuf = nullptr;
static int rmtLeds = 0;
static int rmtPin = -1;
static volatile bool ledReinit = true;

bool ledInit() {
  if (rmtPin >= 0) rmtDeinit(rmtPin);
  if (rmtBuf) { free(rmtBuf); rmtBuf = nullptr; }
  rmtLeds = cfg.ledCount;
  rmtBuf = (rmt_data_t *)malloc(sizeof(rmt_data_t) * MAX_LEDS * 24);   // ~47 KB, reicht für jede Länge
  if (!rmtBuf) { LOG("LED: FEHLER – kein Speicher für %d LEDs", MAX_LEDS); rmtPin = -1; return false; }
  // 10 MHz = 100 ns pro Takt
  if (!rmtInit(cfg.ledPin, RMT_TX_MODE, AMB_RMT_MEM, 10000000)) {
    LOG("LED: FEHLER – Ausgang auf GPIO %d lässt sich nicht starten", cfg.ledPin);
    rmtPin = -1;
    return false;
  }
  rmtPin = cfg.ledPin;
  static const char *ON[] = {"GRB", "RGB", "BRG", "RBG", "GBR", "BGR"};
  LOG("LED: %d LEDs an GPIO %d, Farbreihenfolge %s, Helligkeit %d%%, max. %d mA", rmtLeds, rmtPin, ON[cfg.colorOrder],
      cfg.brightness * 100 / 255, cfg.maxCurrent);
  return true;
}

// Reihenfolge der Bytes auf der Leitung je Farbordnung
static const uint8_t ORDER[6][3] = {
  {1, 0, 2},  // GRB
  {0, 1, 2},  // RGB
  {2, 0, 1},  // BRG
  {0, 2, 1},  // RBG
  {1, 2, 0},  // GBR
  {2, 1, 0},  // BGR
};

void ledShow(const uint8_t *rgb) {
  if (rmtPin < 0 || !rmtBuf) return;
  const uint8_t *ord = ORDER[cfg.colorOrder];
  size_t k = 0;
  for (int i = 0; i < rmtLeds; i++) {
    for (int c = 0; c < 3; c++) {
      uint8_t v = rgb[i * 3 + ord[c]];
      for (int bit = 7; bit >= 0; bit--) {
        if (v & (1 << bit)) {  // 1: 0,8 µs high / 0,45 µs low
          rmtBuf[k].level0 = 1; rmtBuf[k].duration0 = 8;
          rmtBuf[k].level1 = 0; rmtBuf[k].duration1 = 4;
        } else {               // 0: 0,4 µs high / 0,85 µs low
          rmtBuf[k].level0 = 1; rmtBuf[k].duration0 = 4;
          rmtBuf[k].level1 = 0; rmtBuf[k].duration1 = 8;
        }
        k++;
      }
    }
  }
  rmtWrite(rmtPin, rmtBuf, k, RMT_WAIT_FOR_EVER);
}

// ------------------------------------------------------------------ Hilfen
uint32_t hsv(uint8_t h) {  // einfacher Regenbogen
  uint8_t r, g, b, s = h % 85, t = s * 3;
  if (h < 85) { r = 255 - t; g = t; b = 0; }
  else if (h < 170) { r = 0; g = 255 - t; b = t; }
  else { r = t; g = 0; b = 255 - t; }
  return ((uint32_t)r << 16) | ((uint32_t)g << 8) | b;
}

bool sourceAllowed(const IPAddress &ip) {
  uint32_t a = allowIpNum;
  return a == 0 || (uint32_t)ip == a;
}

// ------------------------------------------------------------------ UDP-Empfang
AsyncUDP udpRt, udpDdp, udpTv, udpDisc;

void storeRange(const uint8_t *rgb, int start, int count, int stride) {
  // Kopiert count Farben ab LED start; stride = 3 (RGB) oder 4 (RGBW, Weiß wird aufaddiert)
  portENTER_CRITICAL(&frameMux);
  for (int i = 0; i < count; i++) {
    int led = start + i;
    if (led < 0 || led >= MAX_LEDS) break;
    const uint8_t *p = rgb + i * stride;
    int r = p[0], g = p[1], b = p[2];
    if (stride == 4) { r = min(255, r + p[3]); g = min(255, g + p[3]); b = min(255, b + p[3]); }
    target[led * 3] = r; target[led * 3 + 1] = g; target[led * 3 + 2] = b;
  }
  portEXIT_CRITICAL(&frameMux);
}

void noteRealtime(const IPAddress &src, uint8_t timeoutSec, const char *proto) {
  lastRealtimeMs = millis();
  if (lastRealtimeMs == 0) lastRealtimeMs = 1;
  realtimeTimeoutMs = (timeoutSec == 255) ? 0 : (uint32_t)(timeoutSec == 0 ? 2 : timeoutSec) * 1000;
  rtSourceIp = (uint32_t)src;
  rtProtocol = proto;
  rtFrames++;
}

void onRealtime(AsyncUDPPacket &pkt) {
  rtPackets++;
  if (!sourceAllowed(pkt.remoteIP())) { rtRejected++; return; }
  const uint8_t *d = pkt.data();
  size_t n = pkt.length();
  if (n < 2) return;
  uint8_t proto = d[0], tmo = d[1];
  switch (proto) {
    case 1: {  // WARLS: [1, t, (idx, r, g, b)*]
      portENTER_CRITICAL(&frameMux);
      for (size_t i = 2; i + 3 < n; i += 4) {
        int led = d[i];
        if (led < MAX_LEDS) { target[led * 3] = d[i + 1]; target[led * 3 + 1] = d[i + 2]; target[led * 3 + 2] = d[i + 3]; }
      }
      portEXIT_CRITICAL(&frameMux);
      noteRealtime(pkt.remoteIP(), tmo, "WARLS");
      break;
    }
    case 2:  // DRGB: [2, t, (r, g, b)*]
      storeRange(d + 2, 0, (n - 2) / 3, 3);
      noteRealtime(pkt.remoteIP(), tmo, "DRGB");
      break;
    case 3:  // DRGBW: [3, t, (r, g, b, w)*]
      storeRange(d + 2, 0, (n - 2) / 4, 4);
      noteRealtime(pkt.remoteIP(), tmo, "DRGBW");
      break;
    case 4:  // DNRGB: [4, t, startHi, startLo, (r, g, b)*]
      if (n < 4) return;
      storeRange(d + 4, (d[2] << 8) | d[3], (n - 4) / 3, 3);
      noteRealtime(pkt.remoteIP(), tmo, "DNRGB");
      break;
    default:
      break;
  }
}

void onDdp(AsyncUDPPacket &pkt) {
  rtPackets++;
  if (!sourceAllowed(pkt.remoteIP())) { rtRejected++; return; }
  const uint8_t *d = pkt.data();
  size_t n = pkt.length();
  if (n < 10) return;
  bool hasTime = d[0] & 0x10;
  size_t hdr = hasTime ? 14 : 10;
  if (n < hdr) return;
  if (d[0] & 0x02) return;                     // Abfrage-Paket, keine Farben
  if (d[3] != 0 && d[3] != 1) return;          // nur die Standard-Ausgabe (keine Konfig-/Status-IDs)
  uint32_t offset = ((uint32_t)d[4] << 24) | ((uint32_t)d[5] << 16) | ((uint32_t)d[6] << 8) | d[7];
  uint16_t len = (d[8] << 8) | d[9];
  if (len > n - hdr) len = n - hdr;
  if (len < 3) return;
  storeRange(d + hdr, offset / 3, len / 3, 3);
  noteRealtime(pkt.remoteIP(), 2, "DDP");
}

// Prüft, dass das Paket genau EIN JSON-Objekt ist (Klammern ausgeglichen, Ende = letztes Zeichen),
// damit es beim Einbetten in /api/status nicht "ausbrechen" und eigene Felder setzen kann.
bool jsonObjectOk(const uint8_t *d, size_t n) {
  if (n < 2 || d[0] != '{') return false;
  int depth = 0; bool str = false, esc = false;
  for (size_t i = 0; i < n; i++) {
    uint8_t c = d[i];
    if (c < 0x20) return false;
    if (str) {
      if (esc) esc = false;
      else if (c == '\\') esc = true;
      else if (c == '"') str = false;
      continue;
    }
    if (c == '"') str = true;
    else if (c == '{' || c == '[') { if (++depth > 4) return false; }
    else if (c == '}' || c == ']') { if (--depth < 0) return false; if (depth == 0 && i != n - 1) return false; }
  }
  return depth == 0 && !str;
}

void onTvStatus(AsyncUDPPacket &pkt) {
  if (!sourceAllowed(pkt.remoteIP())) return;
  size_t n = min((size_t)1400, pkt.length());
  if (pkt.length() > 1400 || !jsonObjectOk(pkt.data(), n)) return;
  portENTER_CRITICAL(&tvMux);
  memcpy(tvStatusBuf, pkt.data(), n);
  tvStatusBuf[n] = 0;
  tvStatusMs = millis();
  tvIpNum = (uint32_t)pkt.remoteIP();
  portEXIT_CRITICAL(&tvMux);
}

// Antwort auf "AMBISYNC-DISCOVER": "AMBISYNC 1 leds=<n> port=21324"
void onDiscovery(AsyncUDPPacket &pkt) {
  static const char Q[] = "AMBISYNC-DISCOVER";
  if (pkt.length() < sizeof(Q) - 1 || memcmp(pkt.data(), Q, sizeof(Q) - 1) != 0) return;
  if (!sourceAllowed(pkt.remoteIP())) return;
  char reply[64];
  int n = snprintf(reply, sizeof(reply), "AMBISYNC 1 leds=%d port=%d", cfg.ledCount, PORT_REALTIME);
  pkt.write((const uint8_t *)reply, n);
  discIp = (uint32_t)pkt.remoteIP();
  discCount++;
}

// ------------------------------------------------------------------ Render-Task (Kern 1)
static uint8_t frame[MAX_LEDS * 3];

void renderTask(void *) {
  const uint32_t periodMs = 16;  // ~60 Bilder/s
  uint32_t fpsT0 = millis(), fpsN = 0, rtN0 = 0;
  float idleFade = 0;            // 0 = Echtzeit, 1 = Leerlauf (für weiches Ausblenden)
  for (;;) {
    uint32_t t0 = millis();
    if (ledReinit) {
      ledReinit = false;
      memset(frame, 0, sizeof(frame));
      ledShow(frame);                 // alter Streifen (alte Länge/Pin) aus
      ledInit();
      memset(frame, 0, sizeof(frame));
      int keep = rmtLeds; rmtLeds = MAX_LEDS; ledShow(frame); rmtLeds = keep;   // auch LEDs hinter dem neuen Ende aus
      for (int i = 0; i < MAX_LEDS * 3; i++) outBuf[i] = 0;
    }
    int n = rmtLeds;

    // ---- Quelle wählen ----
    uint32_t lrt = lastRealtimeMs;
    int32_t rtAge = (int32_t)(t0 - lrt);
    bool rt = lrt != 0 && (realtimeTimeoutMs == 0 || rtAge < (int32_t)realtimeTimeoutMs);
    int tm = testMode;
    if (tm != TEST_NONE && testUntilMs && (int32_t)(t0 - testUntilMs) > 0) { testMode = TEST_NONE; tm = TEST_NONE; }

    uint8_t want[MAX_LEDS * 3];
    if (tm != TEST_NONE) {
      for (int i = 0; i < n; i++) {
        uint32_t c = 0;
        switch (tm) {
          case TEST_EDGES: {  // erste LED rot, zweite grün (zeigt Richtung), Rest gedimmt weiß, alle 10 blau
            if (i == 0) c = 0xFF0000; else if (i == 1) c = 0x00FF00; else if (i % 10 == 0) c = 0x0000FF; else c = 0x101010;
            break;
          }
          case TEST_FIRST: c = (i == 0) ? 0xFF0000 : 0; break;
          case TEST_WHITE: c = 0x808080; break;
          case TEST_RAINBOW: c = hsv((uint8_t)(i * 256 / max(1, n) + t0 / 20)); break;
          default: c = 0; break;
        }
        want[i * 3] = c >> 16; want[i * 3 + 1] = (c >> 8) & 0xFF; want[i * 3 + 2] = c & 0xFF;
      }
      idleFade = 0;
    } else {
      idleFade += ((rt ? 0.0f : 1.0f) - idleFade) * 0.08f;   // ~0,5 s Übergang
      uint8_t ir = (cfg.idleColor >> 16) & 0xFF, ig = (cfg.idleColor >> 8) & 0xFF, ib = cfg.idleColor & 0xFF;
      if (cfg.idleMode == 0) { ir = ig = ib = 0; }
      else { ir = ir * cfg.idleBrightness / 255; ig = ig * cfg.idleBrightness / 255; ib = ib * cfg.idleBrightness / 255; }
      portENTER_CRITICAL(&frameMux);
      memcpy(want, target, n * 3);
      portEXIT_CRITICAL(&frameMux);
      for (int i = 0; i < n; i++) {
        want[i * 3] = want[i * 3] * (1 - idleFade) + ir * idleFade;
        want[i * 3 + 1] = want[i * 3 + 1] * (1 - idleFade) + ig * idleFade;
        want[i * 3 + 2] = want[i * 3 + 2] * (1 - idleFade) + ib * idleFade;
      }
      if (idleFade > 0.995f) {  // ganz im Leerlauf: alten Echtzeit-Rest vergessen
        portENTER_CRITICAL(&frameMux);
        memset(target, 0, sizeof(target));
        portEXIT_CRITICAL(&frameMux);
      }
    }

    // ---- weich nachführen (Zwischenbilder zwischen den ~25 TV-Bildern/s) ----
    float k = (tm != TEST_NONE) ? 1.0f : (1.0f - cfg.smoothing / 100.0f);
    if (k < 0.05f) k = 0.05f;
    for (int i = 0; i < n * 3; i++) outBuf[i] += (want[i] - outBuf[i]) * k;

    // ---- Helligkeit + Strombegrenzung ----
    uint32_t sum = 0;
    for (int i = 0; i < n * 3; i++) {
      int v = (int)(outBuf[i] + 0.5f) * cfg.brightness / 255;
      frame[i] = v;
      sum += v;
    }
    int mA = (int)((uint64_t)sum * cfg.mAPerChannel / 255) + n;  // + ~1 mA Ruhestrom je LED
    estCurrent = mA;
    limiting = false;
    if (cfg.maxCurrent > 0 && mA > cfg.maxCurrent) {
      float s = (float)(cfg.maxCurrent - n) / max(1, mA - n);
      if (s < 0) s = 0;
      for (int i = 0; i < n * 3; i++) frame[i] = frame[i] * s;
      limiting = true;
    }

    ledShow(frame);

    // ---- Statistik ----
    fpsN++; outFrames++;
    if (t0 - fpsT0 >= 1000) {
      float dt = (t0 - fpsT0) / 1000.0f;
      outFps = fpsN / dt;
      rtFps = (rtFrames - rtN0) / dt;
      rtN0 = rtFrames; fpsN = 0; fpsT0 = t0;
    }
    uint32_t spent = millis() - t0;
    vTaskDelay(pdMS_TO_TICKS(spent < periodMs ? periodMs - spent : 1));
  }
}

// ------------------------------------------------------------------ Web
WebServer server(80);
DNSServer dns;
bool apMode = false;

// Schutz gegen DNS-Rebinding: nur Anfragen an die eigene IP bzw. den eigenen Namen beantworten.
bool hostOk() {
  String h = server.hostHeader();
  int c = h.indexOf(':'); if (c >= 0) h = h.substring(0, c);
  h.toLowerCase();
  if (h.length() == 0) return true;
  String name = cfg.hostname; name.toLowerCase();
  return h == WiFi.localIP().toString() || h == WiFi.softAPIP().toString() || h == name || h == name + ".local";
}

// Für alles, was liest oder ändert: richtiger Host + eigener Header (fremde Webseiten können ihn
// nicht setzen, ohne dass der Browser vorher fragt - und diese Frage beantwortet der ESP32 nie)
// + Admin-Passwort, falls gesetzt (auch im Einrichtungsmodus).
bool guard() {
  if (!hostOk()) { server.send(403, "text/plain", "Host nicht erlaubt"); return false; }
  if (server.header("X-AmbiSync") != "1") { server.send(403, "text/plain", "Header fehlt"); return false; }
  if (cfg.adminPass.length() == 0) return true;
  if (server.authenticate("admin", cfg.adminPass.c_str())) return true;
  server.requestAuthentication(BASIC_AUTH, "AmbiSync");
  return false;
}
bool authOk() { return guard(); }

String jsonEsc(const String &s) {
  String o; o.reserve(s.length() + 8);
  for (size_t i = 0; i < s.length(); i++) {
    char c = s[i];
    if (c == '"' || c == '\\') { o += '\\'; o += c; }
    else if ((uint8_t)c < 0x20) o += ' ';
    else o += c;
  }
  return o;
}

// TV-Tokens prüfen: nur "name=zahl" und die Wörter pause/run, nichts anderes geht an den Fernseher.
String sanitizeTokens(const String &in) {
  String out;
  int start = 0;
  String s = in + ",";
  for (int i = 0; i < (int)s.length(); i++) {
    char c = s[i];
    if (c == ',' || c == '\n' || c == ' ' || c == ';') {
      String t = s.substring(start, i);
      t.trim();
      start = i + 1;
      if (t.length() == 0 || t.length() > 24) continue;
      bool ok = true;
      t.toLowerCase();
      if (t == "pause" || t == "run") ok = true;
      else {
        int eq = t.indexOf('=');
        if (eq <= 0) ok = false;
        for (int j = 0; ok && j < (int)t.length(); j++) {
          char ch = t[j];
          if (j < eq) ok = isalnum(ch);
          else if (j > eq) ok = isdigit(ch) || ch == '.' || ch == '-';
        }
        if (ok) {
          // Positivliste: nur Einstellungen, die die TV-App kennt und die nichts außer Farben beeinflussen
          static const char *ALLOWED[] = {"map", "rev", "test", "leds", "alpha", "gain", "gamma", "sat", "hdr", "gamut",
                                          "zs", "fps", "ag", "gmin", "gmax", "atau", "ppibars", "psleep", "pz", "peak",
                                          "black", "afast", "acut", "spatial", "bars", "brows", "bthr", "punch", "floor",
                                          "stale", "stalevel", "ui", "uifps", "uithr", "uistale"};
          String k = t.substring(0, eq);
          ok = false;
          for (const char *a : ALLOWED) if (k == a) { ok = true; break; }
        }
      }
      if (ok) { if (out.length()) out += ","; out += t; }
    }
  }
  return out;
}

void handleStatus() {
  uint32_t now = millis();
  uint32_t lrt = lastRealtimeMs;
  int32_t rtAge = lrt ? (int32_t)(now - lrt) : -1;
  if (lrt && rtAge < 0) rtAge = 0;
  bool rt = lrt != 0 && (realtimeTimeoutMs == 0 || rtAge < (int32_t)realtimeTimeoutMs);
  String j = "{";
  j += "\"fw\":\"" FW_VERSION "\",";
  j += "\"ap\":" + String(apMode ? "true" : "false") + ",";
  j += "\"ip\":\"" + (apMode ? WiFi.softAPIP().toString() : WiFi.localIP().toString()) + "\",";
  j += "\"host\":\"" + jsonEsc(cfg.hostname) + "\",";
  j += "\"rssi\":" + String(apMode ? 0 : WiFi.RSSI()) + ",";
  j += "\"uptime\":" + String(now / 1000) + ",";
  j += "\"heap\":" + String(ESP.getFreeHeap()) + ",";
  j += "\"leds\":" + String(cfg.ledCount) + ",";
  j += "\"pin\":" + String(cfg.ledPin) + ",";
  j += "\"ledOk\":" + String(rmtPin >= 0 ? "true" : "false") + ",";
  j += "\"outFps\":" + String(outFps, 1) + ",";
  j += "\"rt\":{\"active\":" + String(rt ? "true" : "false") +
       ",\"fps\":" + String(rtFps, 1) +
       ",\"packets\":" + String((uint32_t)rtPackets) +
       ",\"rejected\":" + String((uint32_t)rtRejected) +
       ",\"proto\":\"" + String(rtProtocol) + "\"" +
       ",\"source\":\"" + (lastRealtimeMs ? IPAddress(rtSourceIp).toString() : String("")) + "\"" +
       ",\"ageMs\":" + String(rtAge) + "},";
  j += "\"current\":" + String(estCurrent) + ",";
  j += "\"limiting\":" + String(limiting ? "true" : "false") + ",";
  j += "\"test\":" + String(testMode) + ",";
  static char tvCopy[1401];
  portENTER_CRITICAL(&tvMux);
  memcpy(tvCopy, tvStatusBuf, sizeof(tvCopy));
  uint32_t tvMs = tvStatusMs, tvIpCopy = tvIpNum;
  portEXIT_CRITICAL(&tvMux);
  j += "\"tvAgeMs\":" + String(tvMs ? (int)(now - tvMs) : -1) + ",";
  j += "\"tvIp\":\"" + (tvMs ? IPAddress(tvIpCopy).toString() : String("")) + "\",";
  j += "\"tv\":" + (tvCopy[0] == '{' ? String(tvCopy) : String("null"));
  j += "}";
  server.send(200, "application/json", j);
}

void handleGetConfig() {
  if (!authOk()) return;
  String j = "{";
  j += "\"ssid\":\"" + jsonEsc(cfg.ssid) + "\",";
  j += "\"host\":\"" + jsonEsc(cfg.hostname) + "\",";
  j += "\"hasAdmin\":" + String(cfg.adminPass.length() ? "true" : "false") + ",";
  j += "\"pin\":" + String(cfg.ledPin) + ",";
  j += "\"leds\":" + String(cfg.ledCount) + ",";
  j += "\"order\":" + String(cfg.colorOrder) + ",";
  j += "\"bri\":" + String(cfg.brightness) + ",";
  j += "\"maxmA\":" + String(cfg.maxCurrent) + ",";
  j += "\"mAch\":" + String(cfg.mAPerChannel) + ",";
  j += "\"smooth\":" + String(cfg.smoothing) + ",";
  j += "\"idle\":" + String(cfg.idleMode) + ",";
  char col[8]; snprintf(col, sizeof(col), "#%06X", (unsigned)(cfg.idleColor & 0xFFFFFF));
  j += "\"idleCol\":\"" + String(col) + "\",";
  j += "\"idleBri\":" + String(cfg.idleBrightness) + ",";
  j += "\"allow\":\"" + jsonEsc(cfg.allowIp) + "\",";
  j += "\"tv\":\"" + jsonEsc(cfg.tvTokens) + "\"";
  j += "}";
  server.send(200, "application/json", j);
}

int argInt(const char *n, int def, int lo, int hi) {
  if (!server.hasArg(n) || server.arg(n).length() == 0) return def;
  return constrain(server.arg(n).toInt(), lo, hi);
}

void handleSetConfig() {
  if (!authOk()) return;
  bool reLed = false, reboot = false;
  if (server.hasArg("admin")) {
    String a = server.arg("admin");
    if (a.length() > 0 && a.length() < 6) { server.send(400, "application/json", "{\"ok\":false,\"error\":\"Passwort zu kurz (min. 6)\"}"); return; }
    if (a.length() >= 6) cfg.adminPass = a;   // leeres Feld = unverändert
  }
  if (server.hasArg("pin") && server.arg("pin").length()) {
    int p = argInt("pin", cfg.ledPin, 0, 48);
    if (!pinSafe(p)) { server.send(400, "application/json", "{\"ok\":false,\"error\":\"Dieser Pin ist nicht erlaubt (Flash/PSRAM/USB)\"}"); return; }
    if (p != cfg.ledPin) { cfg.ledPin = p; reLed = true; }
  }
  if (server.hasArg("leds")) { int l = argInt("leds", cfg.ledCount, 1, MAX_LEDS); if (l != cfg.ledCount) { cfg.ledCount = l; reLed = true; } }
  cfg.colorOrder = argInt("order", cfg.colorOrder, 0, 5);
  cfg.brightness = argInt("bri", cfg.brightness, 0, 255);
  cfg.maxCurrent = argInt("maxmA", cfg.maxCurrent, 0, 100000);
  cfg.mAPerChannel = argInt("mAch", cfg.mAPerChannel, 1, 60);
  cfg.smoothing = argInt("smooth", cfg.smoothing, 0, 95);
  cfg.idleMode = argInt("idle", cfg.idleMode, 0, 1);
  if (server.hasArg("idleCol")) {
    String c = server.arg("idleCol"); c.replace("#", "");
    cfg.idleColor = strtoul(c.c_str(), nullptr, 16) & 0xFFFFFF;
  }
  cfg.idleBrightness = argInt("idleBri", cfg.idleBrightness, 0, 255);
  if (server.hasArg("allow")) { String a = server.arg("allow"); a.trim(); IPAddress tmp; cfg.allowIp = (a.length() == 0 || tmp.fromString(a)) ? a : cfg.allowIp; applyAllowIp(); }
  if (server.hasArg("tv")) cfg.tvTokens = sanitizeTokens(server.arg("tv"));
  if (server.hasArg("host")) { String h = server.arg("host"); h.trim(); if (h.length() > 0 && h.length() < 32) { if (h != cfg.hostname) reboot = true; cfg.hostname = h; } }
  if (server.hasArg("ssid") && cfg.adminPass.length() == 0) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Bitte zuerst ein Admin-Passwort (min. 6 Zeichen) festlegen\"}");
    return;
  }
  if (server.hasArg("ssid")) { String s = server.arg("ssid"); s.trim(); if (s.length()) { cfg.ssid = s; reboot = true; } }
  if (server.hasArg("pass") && server.hasArg("ssid")) { if (server.arg("pass") != "********") { cfg.pass = server.arg("pass"); reboot = true; } }
  saveSettings();
  {
    String keys;
    for (int i = 0; i < server.args(); i++) { if (keys.length()) keys += ", "; keys += server.argName(i); }
    LOG("Web: Einstellungen gespeichert (%s)%s", keys.c_str(), reboot ? " – Neustart folgt" : "");
  }
  if (reLed) ledReinit = true;
  server.send(200, "application/json", String("{\"ok\":true,\"reboot\":") + (reboot ? "true" : "false") + "}");
  if (reboot) { delay(500); ESP.restart(); }
}

void handleTest() {
  if (!authOk()) return;
  String m = server.arg("m");
  int t = TEST_NONE;
  if (m == "edges") t = TEST_EDGES;
  else if (m == "first") t = TEST_FIRST;
  else if (m == "white") t = TEST_WHITE;
  else if (m == "rainbow") t = TEST_RAINBOW;
  else if (m == "off") t = TEST_OFF;
  testMode = t;
  testUntilMs = t == TEST_NONE ? 0 : millis() + 60000;  // Testbild läuft höchstens 60 s
  LOG("Test: %s", t == TEST_NONE ? "beendet" : m.c_str());
  server.send(200, "application/json", "{\"ok\":true}");
}

// Die TV-App fragt hier ihre Einstellungen ab (alle 250 ms). Keine Anmeldung: der Inhalt sind nur
// geprüfte Zahlenwerte, die der Nutzer in dieser Oberfläche gesetzt hat.
void handleTvControl() {
  if (!sourceAllowed(server.client().remoteIP()) && cfg.allowIp.length()) { server.send(403, "text/plain", ""); return; }
  String body = cfg.tvTokens;
  if (body.indexOf("pause") < 0) body = body.length() ? body + ",run" : String("run");
  server.send(200, "text/plain", body);
}

// ---- WLAN-Suche (synchron, dauert 2–5 s; Ergebnis wird zwischengespeichert) ----
static String scanJson = "[]";

void buildScan(int n) {
  int idx[64]; int m = 0;
  for (int i = 0; i < n && m < 64; i++) {           // doppelte Namen zusammenfassen (stärkstes Signal)
    String ss = WiFi.SSID(i);
    if (ss.length() == 0) continue;
    bool dup = false;
    for (int k = 0; k < m; k++) if (WiFi.SSID(idx[k]) == ss) { dup = true; if (WiFi.RSSI(i) > WiFi.RSSI(idx[k])) idx[k] = i; break; }
    if (!dup) idx[m++] = i;
  }
  for (int x = 0; x < m; x++) for (int y = x + 1; y < m; y++) if (WiFi.RSSI(idx[y]) > WiFi.RSSI(idx[x])) { int t = idx[x]; idx[x] = idx[y]; idx[y] = t; }
  String j = "[";
  LOG("WLAN-Suche: %d Netze gefunden%s", m, m ? ":" : " – ist der Router an? Nur 2,4-GHz-Netze werden angezeigt");
  for (int k = 0; k < m; k++) {
    int i = idx[k];
    bool open = WiFi.encryptionType(i) == WIFI_AUTH_OPEN;
    if (k) j += ",";
    j += "{\"ssid\":\"" + jsonEsc(WiFi.SSID(i)) + "\",\"rssi\":" + String(WiFi.RSSI(i)) + ",\"ch\":" + String(WiFi.channel(i)) +
         ",\"open\":" + (open ? "true" : "false") + "}";
    LOG("   %2d. %-28s %4d dBm  Kanal %2d  %s", k + 1, WiFi.SSID(i).c_str(), (int)WiFi.RSSI(i), (int)WiFi.channel(i), open ? "offen" : "verschlüsselt");
  }
  j += "]";
  scanJson = j;
}

// Sucht WLANs. Läuft der Einrichtungsmodus und versucht der ESP32 parallel das Heim-WLAN,
// wird dieser Versuch für die Suche kurz angehalten (sonst verweigert der Treiber die Suche).
int doScan() {
  uint32_t t0 = millis();
  wifi_mode_t mode = WiFi.getMode();
  LOG("WLAN-Suche startet (Modus %s) ...", mode == WIFI_AP ? "AP" : mode == WIFI_STA ? "STA" : mode == WIFI_AP_STA ? "AP+STA" : "aus");
  if (mode == WIFI_AP || mode == WIFI_OFF) { WiFi.mode(mode == WIFI_AP ? WIFI_AP_STA : WIFI_STA); delay(100); }
  bool paused = false;
  if (WiFi.status() != WL_CONNECTED && cfg.ssid.length()) { WiFi.disconnect(false); delay(100); paused = true; }
  WiFi.scanDelete();
  int n = WiFi.scanNetworks(false, false);
  if (n < 0) {
    LOG("WLAN-Suche: 1. Versuch fehlgeschlagen (Code %d) – zweiter Versuch", n);
    if (WiFi.status() != WL_CONNECTED) WiFi.disconnect(false);
    delay(300);
    n = WiFi.scanNetworks(false, false, true, 200);   // passiv: hört nur zu, klappt auch während der Treiber beschäftigt ist
  }
  if (n < 0) LOG("WLAN-Suche FEHLGESCHLAGEN (Code %d) nach %u ms", n, (unsigned)(millis() - t0));
  else { LOG("WLAN-Suche fertig nach %u ms", (unsigned)(millis() - t0)); buildScan(n); }
  WiFi.scanDelete();
  if (paused) { LOG("WLAN: Heim-WLAN \"%s\" wird weiter versucht", cfg.ssid.c_str()); WiFi.begin(cfg.ssid.c_str(), cfg.pass.c_str()); }
  return n;
}

void handleScan() {
  LOG("Web: WLAN-Liste angefragt von %s%s", server.client().remoteIP().toString().c_str(), server.arg("refresh") == "1" ? " (neu suchen)" : "");
  if (!authOk()) { LOG("Web: WLAN-Liste abgelehnt (Host \"%s\", Anmeldung/Header fehlt)", server.hostHeader().c_str()); return; }
  int n = 0;
  if (server.arg("refresh") == "1" || scanJson == "[]") n = doScan();
  server.send(200, "application/json", String("{\"running\":false,\"error\":") + (n < 0 ? String(n) : "0") + ",\"list\":" + scanJson + "}");
}

void handleLog() {
  if (!hostOk()) { server.send(403, "text/plain", ""); return; }
  if (cfg.adminPass.length() && !apMode && !server.authenticate("admin", cfg.adminPass.c_str())) { server.requestAuthentication(BASIC_AUTH, "AmbiSync"); return; }
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "text/plain; charset=utf-8", logText());
}

void handleUpdateDone() {
  if (!authOk()) return;
  bool ok = Update.isFinished() && !Update.hasError();
  server.send(200, "text/plain", ok ? "OK – Neustart" : "Fehler beim Update");
  delay(500);
  if (ok) ESP.restart();
}

void handleUpdateUpload() {
  if (!hostOk() || server.header("X-AmbiSync") != "1") return;
  if (cfg.adminPass.length() && !server.authenticate("admin", cfg.adminPass.c_str())) return;
  HTTPUpload &up = server.upload();
  if (up.status == UPLOAD_FILE_START) {
    testMode = TEST_OFF;
    LOG("Firmware-Update: Upload beginnt (%s)", up.filename.c_str());
    if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
  } else if (up.status == UPLOAD_FILE_WRITE) {
    if (Update.write(up.buf, up.currentSize) != up.currentSize) Update.printError(Serial);
  } else if (up.status == UPLOAD_FILE_END) {
    if (!Update.end(true)) Update.printError(Serial);
    else LOG("Firmware-Update: %u Bytes empfangen, OK", (unsigned)up.totalSize);
  } else if (up.status == UPLOAD_FILE_ABORTED) {
    LOG("Firmware-Update abgebrochen");
    Update.abort();
  }
}

void setupWeb() {
  static const char *hdrs[] = {"X-AmbiSync"};
  server.collectHeaders(hdrs, 1);
  server.on("/", HTTP_GET, []() {
    if (!hostOk()) { server.sendHeader("Location", String("http://") + (apMode ? WiFi.softAPIP() : WiFi.localIP()).toString() + "/", true); server.send(302, "text/plain", ""); return; }
    server.send_P(200, "text/html; charset=utf-8", INDEX_HTML);
  });
  server.on("/api/status", HTTP_GET, []() { if (hostOk()) handleStatus(); else server.send(403, "text/plain", ""); });
  server.on("/api/config", HTTP_GET, handleGetConfig);
  server.on("/api/config", HTTP_POST, handleSetConfig);
  server.on("/api/test", HTTP_POST, handleTest);
  server.on("/api/scan", HTTP_GET, handleScan);
  server.on("/api/log", HTTP_GET, handleLog);
  server.on("/api/reboot", HTTP_POST, []() { if (!authOk()) return; server.send(200, "application/json", "{\"ok\":true}"); delay(300); ESP.restart(); });
  server.on("/tv/control", HTTP_GET, handleTvControl);
  server.on("/update", HTTP_POST, handleUpdateDone, handleUpdateUpload);
  server.onNotFound([]() {
    if (apMode) {  // Captive Portal: alles auf die Einrichtungsseite umleiten
      server.sendHeader("Location", "http://192.168.4.1/", true);
      server.send(302, "text/plain", "");
    } else server.send(404, "text/plain", "Nicht gefunden");
  });
  server.begin();
}

// ------------------------------------------------------------------ WLAN
static uint32_t apSinceMs = 0;

void startAp() {
  apMode = true;
  apSinceMs = millis();
  // Zuerst im reinen STA-Modus suchen (klappt immer), dann AP + STA starten
  WiFi.mode(WIFI_STA);
  WiFi.disconnect(false);
  delay(100);
  doScan();
  WiFi.mode(WIFI_AP_STA);
  if (cfg.ssid.length()) {
    // WLAN ist eingerichtet, aber (noch) nicht erreichbar, z. B. Router startet nach Stromausfall:
    // Einrichtungs-WLAN öffnen UND weiter das Heim-WLAN versuchen.
    WiFi.begin(cfg.ssid.c_str(), cfg.pass.c_str());
    WiFi.setAutoReconnect(true);
  }
  WiFi.softAP(AP_SSID, AP_PASS);
  dns.start(53, "*", WiFi.softAPIP());
  LOG("=== EINRICHTUNGSMODUS ===");
  LOG("1. Mit dem WLAN \"%s\" verbinden (Passwort: %s)", AP_SSID, AP_PASS);
  LOG("2. http://%s öffnen, Heim-WLAN aus der Liste wählen, Passwort eingeben", WiFi.softAPIP().toString().c_str());
  LOG("   oder hier im Monitor:  wifi \"WLAN-Name\" Passwort   (Befehl 'hilfe' zeigt alle Befehle)");
  if (cfg.ssid.length()) LOG("Heim-WLAN \"%s\" ist gespeichert und wird im Hintergrund weiter versucht", cfg.ssid.c_str());
  testMode = TEST_FIRST;   // erste LED rot = "bitte einrichten"
  testUntilMs = 0;
}

bool connectWifi() {
  if (cfg.ssid.length() == 0) return false;
  WiFi.mode(WIFI_STA);
  WiFi.setHostname(cfg.hostname.c_str());
  WiFi.setSleep(false);   // kein Stromsparen: sonst ruckelt der UDP-Empfang
  WiFi.begin(cfg.ssid.c_str(), cfg.pass.c_str());
  LOG("WLAN: verbinde mit \"%s\" (max. 20 s) ...", cfg.ssid.c_str());
  uint32_t t0 = millis();
  int last = -1;
  while (WiFi.status() != WL_CONNECTED && millis() - t0 < 20000) {
    int st = WiFi.status();
    if (st != last) { LOG("WLAN: Status: %s", wlStatusName(st)); last = st; }
    delay(250);
  }
  if (WiFi.status() != WL_CONNECTED) {
    LOG("WLAN: KEINE Verbindung nach 20 s (%s) -> Einrichtungsmodus", wlStatusName(WiFi.status()));
    LOG("      Häufige Ursachen: falsches Passwort, 5-GHz-Netz (ESP32 kann nur 2,4 GHz), Router zu weit weg");
    return false;
  }
  WiFi.setAutoReconnect(true);
  LOG("WLAN: verbunden – IP %s, Signal %d dBm, Kanal %d", WiFi.localIP().toString().c_str(), (int)WiFi.RSSI(), (int)WiFi.channel());
  LOG("Weboberfläche: http://%s  oder  http://%s.local", WiFi.localIP().toString().c_str(), cfg.hostname.c_str());
  return true;
}

// ------------------------------------------------------------------ Protokoll & Befehle
void onWifiEvent(arduino_event_id_t ev, arduino_event_info_t info) {
  static uint32_t lastDiscLog = 0;
  switch (ev) {
    case ARDUINO_EVENT_WIFI_STA_CONNECTED: LOG("WLAN: mit dem Router verbunden, warte auf IP-Adresse ..."); break;
    case ARDUINO_EVENT_WIFI_STA_GOT_IP: LOG("WLAN: IP-Adresse %s erhalten", IPAddress(info.got_ip.ip_info.ip.addr).toString().c_str()); break;
    case ARDUINO_EVENT_WIFI_STA_DISCONNECTED: {
      int r = info.wifi_sta_disconnected.reason;
      if (millis() - lastDiscLog < 3000) break;   // nicht fluten, der Treiber versucht es mehrfach pro Sekunde
      lastDiscLog = millis();
      const char *hint = "";
      if (r == 15 || r == 204 || r == 202 || r == 2) hint = " -> Passwort falsch?";
      else if (r == 201) hint = " -> Netz nicht gefunden (Name falsch oder 5 GHz – ESP32 kann nur 2,4 GHz)";
      else if (r == 203) hint = " -> Verbindung abgelehnt (Router-Filter?)";
      LOG("WLAN: getrennt, Grund %d (%s)%s", r, WiFi.disconnectReasonName((wifi_err_reason_t)r), hint);
      break;
    }
    case ARDUINO_EVENT_WIFI_AP_STACONNECTED: LOG("Einrichtung: ein Gerät hat sich mit \"%s\" verbunden", AP_SSID); break;
    case ARDUINO_EVENT_WIFI_AP_STADISCONNECTED: LOG("Einrichtung: Gerät getrennt"); break;
    default: break;
  }
}

void printStatus() {
  uint32_t now = millis();
  uint32_t lrt = lastRealtimeMs;
  bool rt = lrt != 0 && (realtimeTimeoutMs == 0 || (int32_t)(now - lrt) < (int32_t)realtimeTimeoutMs);
  uint32_t tvMs = tvStatusMs;
  LOG("STATUS | WLAN: %s | LEDs: %d an GPIO %d%s, %.0f Bilder/s | Farben: %s | TV: %s | Strom ~%.2f A%s | Speicher %u KB",
      apMode ? "Einrichtungsmodus" : (WiFi.status() == WL_CONNECTED ? (WiFi.localIP().toString() + " " + String(WiFi.RSSI()) + " dBm").c_str() : wlStatusName(WiFi.status())),
      cfg.ledCount, cfg.ledPin, rmtPin >= 0 ? "" : " (FEHLER)", (float)outFps,
      rt ? (String(rtProtocol) + " " + String((float)rtFps, 1) + "/s von " + IPAddress(rtSourceIp).toString()).c_str() : "keine",
      tvMs ? ((int32_t)(now - tvMs) < 6000 ? ("verbunden (" + IPAddress(tvIpNum).toString() + ")").c_str() : ("still seit " + String((now - tvMs) / 1000) + " s").c_str()) : "noch nie gemeldet",
      estCurrent / 1000.0f, limiting ? " (begrenzt)" : "", (unsigned)(ESP.getFreeHeap() / 1024));
}

void printHelp() {
  outRaw("");
  outRaw("------------------------------ AmbiSync Befehle ------------------------------");
  outRaw("  hilfe                       diese Übersicht");
  outRaw("  status                      Statuszeile sofort ausgeben");
  outRaw("  scan                        WLAN-Netze suchen und auflisten");
  outRaw("  wifi \"Name\" Passwort        Heim-WLAN setzen und neu starten (Anführungszeichen bei Leerzeichen)");
  outRaw("  admin Passwort              Admin-Passwort der Weboberfläche setzen (min. 6 Zeichen)");
  outRaw("  leds Anzahl                 Anzahl LEDs (1-490)");
  outRaw("  pin GPIO                    Daten-Pin der LEDs");
  outRaw("  test kanten|weiss|regenbogen|erste|aus|ende");
  outRaw("  neustart                    ESP32 neu starten");
  outRaw("  werkreset                   ALLE Einstellungen löschen (WLAN, Passwort, LEDs)");
  outRaw("------------------------------------------------------------------------------");
  outRaw("");
}

// zerlegt  wifi "Mein WLAN" geheim123   bzw.  wifi MeinWLAN geheim123
static bool parseTwo(const String &rest, String &a, String &b) {
  String r = rest; r.trim();
  if (r.startsWith("\"")) {
    int q = r.indexOf('"', 1); if (q < 0) return false;
    a = r.substring(1, q); b = r.substring(q + 1); b.trim();
  } else {
    int sp = r.indexOf(' ');
    if (sp < 0) { a = r; b = ""; } else { a = r.substring(0, sp); b = r.substring(sp + 1); b.trim(); }
  }
  return a.length() > 0;
}

void handleSerial() {
  static String line;
  while (true) {
    int ci = Serial.available() ? Serial.read() : -1;
#ifdef AMB_USB_MIRROR
    if (ci < 0 && usbLog.available()) ci = usbLog.read();
#endif
    if (ci < 0) break;
    char c = (char)ci;
    if (c == '\r') continue;
    if (c != '\n') { if (line.length() < 200) line += c; continue; }
    String cmd = line; line = ""; cmd.trim();
    if (cmd.length() == 0) continue;
    int sp = cmd.indexOf(' ');
    String w = (sp < 0 ? cmd : cmd.substring(0, sp)); w.toLowerCase();
    String rest = sp < 0 ? "" : cmd.substring(sp + 1);
    LOG("> %s", w == "wifi" || w == "admin" ? (w + " ...").c_str() : cmd.c_str());   // Passwörter nicht wiederholen
    if (w == "hilfe" || w == "help" || w == "?") printHelp();
    else if (w == "status") printStatus();
    else if (w == "scan") doScan();
    else if (w == "wifi") {
      String ssid, pass;
      if (!parseTwo(rest, ssid, pass)) { LOG("Bitte so:  wifi \"WLAN-Name\" Passwort"); continue; }
      cfg.ssid = ssid; cfg.pass = pass; saveSettings();
      LOG("WLAN \"%s\" gespeichert (Passwort %d Zeichen) – Neustart ...", ssid.c_str(), pass.length());
      if (cfg.adminPass.length() == 0) LOG("Hinweis: noch kein Admin-Passwort – mit 'admin <passwort>' setzen");
      delay(300); ESP.restart();
    }
    else if (w == "admin") {
      rest.trim();
      if (rest.length() < 6) { LOG("Passwort zu kurz (min. 6 Zeichen)"); continue; }
      cfg.adminPass = rest; saveSettings(); LOG("Admin-Passwort gesetzt (Benutzer: admin)");
    }
    else if (w == "leds") { int n = rest.toInt(); if (n < 1 || n > MAX_LEDS) LOG("1 bis %d", MAX_LEDS); else { cfg.ledCount = n; saveSettings(); ledReinit = true; LOG("LED-Anzahl: %d", n); } }
    else if (w == "pin") { int p = rest.toInt(); if (!pinSafe(p)) LOG("GPIO %d ist nicht erlaubt (Flash/PSRAM/USB)", p); else { cfg.ledPin = p; saveSettings(); ledReinit = true; LOG("LED-Pin: GPIO %d", p); } }
    else if (w == "test") {
      rest.trim(); rest.toLowerCase();
      int t = rest == "kanten" ? TEST_EDGES : rest == "weiss" ? TEST_WHITE : rest == "regenbogen" ? TEST_RAINBOW :
              rest == "erste" ? TEST_FIRST : rest == "aus" ? TEST_OFF : TEST_NONE;
      testMode = t; testUntilMs = t == TEST_NONE ? 0 : millis() + 60000;
      LOG("Test: %s", t == TEST_NONE ? "beendet" : rest.c_str());
    }
    else if (w == "neustart" || w == "reboot") { LOG("Neustart ..."); delay(200); ESP.restart(); }
    else if (w == "werkreset") {
      prefs.begin("ambisync", false); prefs.clear(); prefs.end();
      LOG("Alle Einstellungen gelöscht – Neustart in den Einrichtungsmodus ..."); delay(300); ESP.restart();
    }
    else LOG("Unbekannter Befehl – 'hilfe' zeigt alle Befehle");
  }
}

// Ereignisse aus den UDP-Empfängern (laufen in anderen Tasks) hier sauber ins Protokoll schreiben
void logEvents() {
  static bool rtWas = false; static uint32_t srcWas = 0, rejWas = 0, discWas = 0;
  static int tvWas = 0;   // 0 nie, 1 verbunden, 2 still
  static uint32_t lastRej = 0;
  uint32_t now = millis(), lrt = lastRealtimeMs;
  bool rt = lrt != 0 && (realtimeTimeoutMs == 0 || (int32_t)(now - lrt) < (int32_t)realtimeTimeoutMs);
  if (rt && (!rtWas || rtSourceIp != srcWas)) LOG("Farben: Empfang gestartet – %s von %s", (const char *)rtProtocol, IPAddress(rtSourceIp).toString().c_str());
  if (!rt && rtWas) LOG("Farben: Empfang beendet (Zeitüberschreitung) -> %s", cfg.idleMode ? "Leerlauf-Farbe" : "LEDs aus");
  rtWas = rt; srcWas = rtSourceIp;
  uint32_t tvMs = tvStatusMs;
  int tv = tvMs == 0 ? 0 : ((int32_t)(now - tvMs) < 6000 ? 1 : 2);
  if (tv == 1 && tvWas != 1) LOG("Fernseher: AmbiSync-App meldet sich von %s", IPAddress(tvIpNum).toString().c_str());
  if (tv == 2 && tvWas == 1) LOG("Fernseher: keine Meldung mehr seit 6 s (TV aus/Standby oder App beendet)");
  tvWas = tv;
  uint32_t d = discCount;
  if (d != discWas) { LOG("Fernseher: Suche nach dem ESP32 von %s – beantwortet (%d LEDs)", IPAddress((uint32_t)discIp).toString().c_str(), cfg.ledCount); discWas = d; }
  uint32_t rj = rtRejected;
  if (rj != rejWas && now - lastRej > 10000) { LOG("Farben: %u Pakete von fremden IPs abgelehnt (Filter: %s)", (unsigned)(rj - rejWas), cfg.allowIp.c_str()); rejWas = rj; lastRej = now; }
}

// ------------------------------------------------------------------ Start
void setup() {
  Serial.begin(115200);
#ifdef AMB_USB_MIRROR
  usbLog.setTxTimeoutMs(0);   // nicht blockieren, wenn kein PC angeschlossen ist
  usbLog.begin();
#endif
  delay(1500);                // Zeit, damit der serielle Monitor nach dem Neustart wieder verbunden ist
  outRaw("");
  outRaw("==============================================================");
  outRaw("  AmbiSync " FW_VERSION " – Ambilight-Empfänger für den Fernseher");
  outRaw("  Befehle: 'hilfe' eingeben (Monitor auf 115200 Baud, Zeilenende: Neue Zeile)");
  outRaw("==============================================================");
  LOG("Chip: %s Rev %d, %d Kerne, %u MHz, Flash %u MB, PSRAM %s, MAC %s", ESP.getChipModel(), ESP.getChipRevision(),
      ESP.getChipCores(), (unsigned)ESP.getCpuFreqMHz(), (unsigned)(ESP.getFlashChipSize() / 1048576), psramFound() ? "ja" : "nein",
      WiFi.macAddress().c_str());
  static const char *RR[] = {"unbekannt", "Einschalten", "extern", "Software-Neustart", "Absturz", "Watchdog (Interrupt)",
                             "Watchdog (Task)", "Watchdog", "Tiefschlaf", "Spannungseinbruch (Netzteil zu schwach?)", "SDIO"};
  int rr = (int)esp_reset_reason();
  LOG("Start-Grund: %s", rr >= 0 && rr <= 10 ? RR[rr] : "?");
  loadSettings();
  LOG("Einstellungen: WLAN \"%s\", Name %s.local, Admin-Passwort %s, %d LEDs an GPIO %d, Filter-IP %s",
      cfg.ssid.length() ? cfg.ssid.c_str() : "(keins)", cfg.hostname.c_str(), cfg.adminPass.length() ? "gesetzt" : "NICHT gesetzt",
      cfg.ledCount, cfg.ledPin, cfg.allowIp.length() ? cfg.allowIp.c_str() : "(alle)");
  WiFi.onEvent(onWifiEvent);
  memset(target, 0, sizeof(target));

  xTaskCreatePinnedToCore(renderTask, "render", 8192, nullptr, 3, nullptr, 1);

  if (!connectWifi()) startAp();
  else {
    if (MDNS.begin(cfg.hostname.c_str())) LOG("mDNS: %s.local aktiv", cfg.hostname.c_str());
    else LOG("mDNS: konnte %s.local nicht starten – IP-Adresse benutzen", cfg.hostname.c_str());
    MDNS.addService("http", "tcp", 80);
    MDNS.addService("wled", "tcp", 80);   // damit Tools wie HyperHDR ihn finden
  }

  udpRt.listen(PORT_REALTIME);
  udpRt.onPacket(onRealtime);
  udpDdp.listen(PORT_DDP);
  udpDdp.onPacket(onDdp);
  udpTv.listen(PORT_TVSTATUS);
  udpTv.onPacket(onTvStatus);
  if (!apMode) { udpDisc.listen(PORT_DISCOVERY); udpDisc.onPacket(onDiscovery); }
  LOG("UDP: Farben auf Port %d (WLED) und %d (DDP), TV-Status %d, Suche %d%s", PORT_REALTIME, PORT_DDP, PORT_TVSTATUS,
      PORT_DISCOVERY, apMode ? " (aus im Einrichtungsmodus)" : "");

  setupWeb();
  LOG("Web: Server läuft auf Port 80");
  if (!apMode) LOG("Bereit. Warte auf den Fernseher ... (Statuszeile alle 10 s, 'hilfe' für Befehle)");
  if (!apMode && cfg.adminPass.length() == 0) LOG("WARNUNG: kein Admin-Passwort gesetzt – im Monitor: admin <passwort>");
}

void loop() {
  handleSerial();
  logEvents();
  static uint32_t lastStatus = 0, lastApHint = 0;
  if (millis() - lastStatus > 10000) { lastStatus = millis(); printStatus(); }
  if (apMode && millis() - lastApHint > 30000) {
    lastApHint = millis();
    LOG("Einrichtung: WLAN \"%s\" (Passwort %s) -> http://192.168.4.1   oder hier: wifi \"Name\" Passwort", AP_SSID, AP_PASS);
  }
  if (apMode) {
    dns.processNextRequest();
    if (cfg.ssid.length()) {
      if (WiFi.status() == WL_CONNECTED) { LOG("Heim-WLAN wieder da – Neustart in den Normalbetrieb"); delay(200); ESP.restart(); }
      if (millis() - apSinceMs > 10UL * 60 * 1000) { LOG("10 min im Einrichtungsmodus – Neustart, Heim-WLAN neu versuchen"); delay(200); ESP.restart(); }
    }
  }
  server.handleClient();
  // WLAN weg? Nach 60 s ohne Verbindung neu starten (Einstellungen bleiben erhalten).
  static uint32_t lostSince = 0;
  if (!apMode && WiFi.status() != WL_CONNECTED) {
    if (!lostSince) lostSince = millis();
    if (millis() - lostSince > 60000) { LOG("WLAN seit 60 s weg – Neustart"); delay(200); ESP.restart(); }
  } else lostSince = 0;
  delay(2);
}

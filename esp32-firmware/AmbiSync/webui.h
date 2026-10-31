#pragma once
// Weboberfläche von AmbiSync (eine Seite, kein Internet nötig)
static const char INDEX_HTML[] PROGMEM = R"HTML(<!doctype html>
<html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>AmbiSync</title>
<style>
:root{--bg:#0f1115;--card:#181b22;--line:#2a2f3a;--text:#e9ecf1;--mute:#8b93a3;--acc:#5b8cff;--ok:#3fbf7f;--warn:#e0a63a;--bad:#e2566a}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.45 system-ui,-apple-system,Segoe UI,Roboto,sans-serif}
header{padding:18px 16px 6px;max-width:980px;margin:auto;display:flex;align-items:baseline;gap:12px;flex-wrap:wrap}
h1{font-size:22px;margin:0}header small{color:var(--mute)}
main{max-width:980px;margin:auto;padding:8px 16px 40px;display:grid;gap:14px;grid-template-columns:repeat(auto-fit,minmax(300px,1fr))}
section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
h2{font-size:15px;margin:0 0 10px;color:#cfd6e4;letter-spacing:.02em}
.kv{display:grid;grid-template-columns:auto 1fr;gap:4px 12px;font-size:14px}.kv b{font-weight:500;color:var(--mute)}
.dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:6px;background:var(--mute)}
.ok{background:var(--ok)}.warn{background:var(--warn)}.bad{background:var(--bad)}
label{display:block;font-size:13px;color:var(--mute);margin:10px 0 4px}
input,select{width:100%;padding:8px 10px;border-radius:8px;border:1px solid var(--line);background:#0f1218;color:var(--text);font:inherit}
input[type=range]{padding:0}input[type=color]{height:38px;padding:2px}
.row{display:grid;grid-template-columns:1fr 1fr;gap:10px}
button{background:var(--acc);color:#fff;border:0;border-radius:8px;padding:9px 14px;font:inherit;cursor:pointer;margin:10px 6px 0 0}
button.sec{background:#2a3140}button.danger{background:var(--bad)}
.btns{display:flex;flex-wrap:wrap}.hint{font-size:12px;color:var(--mute);margin-top:6px}
.full{grid-column:1/-1}.msg{position:fixed;left:50%;bottom:18px;transform:translateX(-50%);background:#222a38;padding:10px 16px;border-radius:10px;display:none}
details summary{cursor:pointer;color:var(--mute);margin-top:10px}
code{background:#0f1218;padding:1px 5px;border-radius:5px}
.nets{border:1px solid var(--line);border-radius:8px;max-height:340px;overflow:auto;background:#0f1218}
.net{display:flex;align-items:center;gap:10px;padding:11px 12px;border-bottom:1px solid var(--line);cursor:pointer}
.net:last-child{border-bottom:0}.net:hover{background:#1a1f29}.net.sel{background:#1d2a45;box-shadow:inset 3px 0 0 var(--acc)}
.net .n{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.net small{color:var(--mute);white-space:nowrap}
.bars{display:inline-flex;align-items:flex-end;gap:2px;height:15px}.bars i{width:4px;background:#3a4252;border-radius:1px}.bars i.on{background:var(--ok)}
.pw{display:flex;gap:6px}.pw input{flex:1}.pw button{margin:0;padding:0 12px}
.picked{margin-top:10px;padding:8px 10px;border-radius:8px;background:#1d2a45}
.step{color:var(--text);font-weight:600;font-size:14px;margin-top:14px}
pre.log{background:#0a0c10;border:1px solid var(--line);border-radius:8px;padding:8px;font:11px/1.4 ui-monospace,Consolas,monospace;max-height:260px;overflow:auto;white-space:pre-wrap;word-break:break-word;margin:6px 0 0}
</style></head><body>
<header><h1>AmbiSync</h1><small id="hdr">verbinde …</small></header>
<main>

<section id="setup" class="full" style="display:none">
<h2>Einrichtung: Heim-WLAN</h2>
<div class="hint">Der ESP32 ist im Einrichtungsmodus. Wähle dein WLAN, danach startet er neu und ist unter <code>http://ambisync.local</code> erreichbar. Der ESP32 kann nur <b>2,4-GHz</b>-Netze.</div>
<label class="step">1. Dein WLAN antippen</label>
<div id="nets" class="nets"><div class="hint" style="padding:10px">suche WLAN-Netze …</div></div>
<button class="sec" onclick="scan(1)">Neu suchen</button>
<div id="picked" class="picked" style="display:none">Gewählt: <b id="pickedName"></b></div>
<details><summary>Mein WLAN steht nicht in der Liste (verstecktes Netz)</summary>
<label>WLAN-Name von Hand</label><input id="ssidMan" placeholder="genauer WLAN-Name" oninput="manual()"></details>
<label class="step">2. WLAN-Passwort</label>
<div class="pw"><input id="wpass" type="password" placeholder="zuerst oben ein WLAN antippen"><button class="sec" onclick="eye('wpass',this)">zeigen</button></div>
<label class="step">3. Admin-Passwort für diese Oberfläche (min. 6 Zeichen, Pflicht)</label>
<div class="pw"><input id="wadmin" type="password"><button class="sec" onclick="eye('wadmin',this)">zeigen</button></div>
<div class="hint">Benutzername beim Anmelden: <code>admin</code>. Ohne dieses Passwort könnte jeder in deinem WLAN den ESP32 umstellen.</div>
<button onclick="saveWifi()">Verbinden</button>
<details open><summary>Protokoll des ESP32 (live)</summary><pre id="log1" class="log">lädt …</pre></details>
</section>

<section>
<h2>Status</h2>
<div class="kv">
<b>Fernseher</b><span id="sTv">–</span>
<b>Quelle</b><span id="sSrc">–</span>
<b>Farben/s</b><span id="sRt">–</span>
<b>LED-Ausgabe</b><span id="sOut">–</span>
<b>Strom (geschätzt)</b><span id="sCur">–</span>
<b>WLAN</b><span id="sWifi">–</span>
<b>Laufzeit</b><span id="sUp">–</span>
</div>
</section>

<section>
<h2>Fernseher-App</h2>
<div class="kv">
<b>Bildquelle</b><span id="tBack">–</span>
<b>Bilder/s</b><span id="tFps">–</span>
<b>Menü-Erkennung</b><span id="tUi">–</span>
<b>Menü-Anteil</b><span id="tCov">–</span>
<b>Status</b><span id="tStat" style="word-break:break-word">–</span>
</div>
<div class="hint">Diese Werte schickt die TV-App alle 2 s. Es werden keine Bilder übertragen.</div>
</section>

<section>
<h2>Test</h2>
<div class="hint">Testbilder laufen 60 s und haben Vorrang vor dem Fernseher.</div>
<div class="btns">
<button class="sec" onclick="test('edges')">Richtung prüfen</button>
<button class="sec" onclick="test('first')">Erste LED</button>
<button class="sec" onclick="test('white')">Weiß</button>
<button class="sec" onclick="test('rainbow')">Regenbogen</button>
<button class="sec" onclick="test('off')">Alle aus</button>
<button onclick="test('none')">Test beenden</button>
</div>
<div class="hint">„Richtung prüfen“: erste LED rot, zweite grün, jede 10. blau. So findest du Start und Laufrichtung des Streifens.</div>
</section>

<section>
<h2>LED-Streifen</h2>
<label>Art des Streifens</label><select id="type" onchange="typeUi()"><option value="0">WS2812B – jede LED einzeln (echtes Ambilight)</option><option value="1">normaler RGB-Streifen (R, G, B, +) – eine Farbe für alles</option></select>
<div id="rgbBox" style="display:none">
<div class="row" style="grid-template-columns:1fr 1fr 1fr"><div><label>Rot an GPIO</label><input id="pinR" type="number" min="0" max="48"></div>
<div><label>Grün an GPIO</label><input id="pinG" type="number" min="0" max="48"></div><div><label>Blau an GPIO</label><input id="pinB" type="number" min="0" max="48"></div></div>
<div class="hint">Jeder Pin schaltet über einen MOSFET die Minus-Leitung der Farbe. Der Streifen zeigt die Durchschnittsfarbe des Bildes.</div></div>
<div class="row"><div id="pinBox"><label>Daten-Pin (GPIO)</label><input id="pin" type="number" min="0" max="48"></div>
<div><label>Anzahl LEDs / Zonen</label><input id="leds" type="number" min="1" max="490"></div></div>
<div id="orderBox"><label>Farbreihenfolge</label><select id="order"><option value="0">GRB (WS2812B, üblich)</option><option value="1">RGB</option><option value="2">BRG</option><option value="3">RBG</option><option value="4">GBR</option><option value="5">BGR</option></select></div>
<label>Helligkeit <span id="briV"></span></label><input id="bri" type="range" min="0" max="255" oninput="briV.textContent=Math.round(this.value/2.55)+' %'">
<div class="row"><div><label>Max. Strom (mA, 0 = aus)</label><input id="maxmA" type="number" min="0" step="500"></div>
<div><label>mA pro Farbkanal</label><input id="mAch" type="number" min="1" max="60"></div></div>
<div class="hint">Strombegrenzung schützt Netzteil und Streifen: z. B. 5-V-Netzteil mit 15 A → 13000 mA eintragen.</div>
<label>Weiche Übergänge <span id="smV"></span></label><input id="smooth" type="range" min="0" max="95" oninput="smV.textContent=this.value+' %'">
<label>Wenn der Fernseher nichts sendet</label><select id="idle"><option value="0">LEDs aus</option><option value="1">feste Farbe</option></select>
<div class="row"><div><label>Farbe</label><input id="idleCol" type="color"></div>
<div><label>Helligkeit</label><input id="idleBri" type="range" min="0" max="255"></div></div>
<button onclick="saveLed()">Speichern</button>
</section>

<section>
<h2>Fernseher-Einstellungen</h2>
<div class="hint">Die TV-App holt sich diese Werte selbst ab. Leer = Standard der App.</div>
<label>Menü &amp; Startseite einbeziehen</label>
<select id="t_ui"><option value="">Standard (automatisch)</option><option value="1">automatisch</option><option value="2">Transparenz-Modus</option><option value="3">Dunkel-Modus</option><option value="0">aus (nur Video)</option></select>
<div class="row"><div><label>Helligkeit (gain, Std. 1.0)</label><input id="t_gain" type="number" step="0.05" min="0.1" max="8"></div>
<div><label>Sättigung (Std. 1.0)</label><input id="t_sat" type="number" step="0.05" min="0" max="4"></div></div>
<div class="row"><div><label>Reaktion (alpha, Std. 0.25)</label><input id="t_alpha" type="number" step="0.01" min="0.02" max="1"></div>
<div><label>Grundlicht (floor, Std. 0)</label><input id="t_floor" type="number" step="1" min="0" max="255"></div></div>
<div class="row"><div><label>Start-LED verschieben (map)</label><input id="t_map" type="number" step="1"></div>
<div><label>Laufrichtung umkehren</label><select id="t_rev"><option value="">Standard</option><option value="0">nein</option><option value="1">ja</option></select></div></div>
<div class="row"><div><label>Kanten-Testbild vom TV</label><select id="t_test"><option value="">aus</option><option value="1">an (oben rot, rechts grün, unten blau, links gelb)</option></select></div>
<div><label>Menü-Bilder/s (Std. 6)</label><input id="t_uifps" type="number" min="1" max="15"></div></div>
<details><summary>Alle Werte als Text (für Fortgeschrittene)</summary>
<label>z. B. <code>gamma=1.8,ag=0,black=8,uistale=5000</code></label><input id="t_extra">
</details>
<button onclick="saveTv()">An Fernseher senden</button>
<div class="hint">Mapping und Menü-Modus wirken sofort. Werte, die du wieder leerst, gelten erst nach einem TV-Neustart wieder als Standard.</div>
</section>

<section>
<h2>System &amp; Sicherheit</h2>
<label>Gerätename (ergibt <code>name.local</code>)</label><input id="host">
<label>Admin-Passwort (min. 6 Zeichen, leer = keins)</label><input id="admin" type="password" placeholder="unverändert lassen">
<div class="hint" id="adminHint"></div>
<label>Farben nur von dieser IP annehmen (Fernseher-IP, leer = alle)</label><input id="allow" placeholder="z. B. 192.168.178.50">
<button onclick="saveSys()">Speichern</button>
<details><summary>WLAN wechseln</summary>
<label>WLAN-Name</label><input id="ssid2" list="netsDl" placeholder="tippen oder aus der Liste wählen" onfocus="if(!NETS.length)scan(0)"><datalist id="netsDl"></datalist><label>Passwort</label><input id="pass2" type="password">
<button class="sec" onclick="saveWifi2()">Speichern &amp; neu starten</button></details>
<details><summary>Firmware-Update</summary>
<input id="fw" type="file" accept=".bin"><button class="sec" onclick="upd()">Hochladen</button><div class="hint" id="updMsg"></div></details>
<details><summary>Protokoll (live)</summary><pre id="log2" class="log">–</pre></details>
<button class="danger" onclick="post('/api/reboot').then(()=>toast('Startet neu …'))">Neustart</button>
</section>

</main>
<div class="msg" id="msg"></div>
<script>
const $=id=>document.getElementById(id);
const TVKEYS=['ui','gain','sat','alpha','floor','map','rev','test','uifps'];
function toast(t){const m=$('msg');m.textContent=t;m.style.display='block';clearTimeout(m._t);m._t=setTimeout(()=>m.style.display='none',2500)}
const H={'X-AmbiSync':'1'};
async function post(url,data){const r=await fetch(url,{method:'POST',headers:H,body:new URLSearchParams(data||{})});
 if(r.status==401)throw new Error('Passwort nötig');let j={};try{j=await r.json()}catch(e){}
 if(!r.ok||j.ok===false)throw new Error(j.error||('Fehler '+r.status));return j}
function dot(c){return '<span class="dot '+c+'"></span>'}
function fmtUp(s){const h=Math.floor(s/3600),m=Math.floor(s%3600/60);return h+' h '+m+' min'}
function esc(s){return String(s==null?'':s).replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]))}
const BACK={video_plane:'Video-Ebene',ui_only:'nur Menü/Startseite','ppi(auto)':'Samsung-Farbmessung (geschützter Inhalt)','ppi(forced)':'Samsung-Farbmessung',file:'Bildschirmfoto'};
async function refresh(){
 try{const s=await (await fetch('/api/status')).json();
  $('hdr').textContent=(s.ap?'Einrichtungsmodus':s.host+'.local · '+s.ip)+' · Firmware '+s.fw;
  $('setup').style.display=s.ap?'block':'none';
  const tvOk=s.tvAgeMs>=0&&s.tvAgeMs<6000;
  $('sTv').innerHTML=tvOk?dot('ok')+'verbunden ('+esc(s.tvIp)+')':(s.tvAgeMs<0?dot('')+'noch nie gemeldet':dot('warn')+'seit '+Math.round(s.tvAgeMs/1000)+' s still');
  $('sSrc').innerHTML=s.rt.active?dot('ok')+esc(s.rt.proto)+' von '+esc(s.rt.source):dot('')+'keine'+(s.rt.rejected?' ('+s.rt.rejected+' abgelehnt)':'');
  $('sRt').textContent=(Number(s.rt.fps)||0).toFixed(1);
  $('sOut').innerHTML=(s.ledOk?dot('ok'):dot('bad')+'Fehler · ')+s.leds+' LEDs an GPIO '+s.pin+' · '+s.outFps.toFixed(0)+' Bilder/s';
  $('sCur').innerHTML=(s.limiting?dot('warn'):'')+(s.current/1000).toFixed(2)+' A'+(s.limiting?' (begrenzt)':'');
  $('sWifi').textContent=s.ap?'Einrichtungs-WLAN':s.rssi+' dBm';
  $('sUp').textContent=fmtUp(s.uptime);
  const t=s.tv;
  if(t&&tvOk){
   $('tBack').textContent=(BACK[t.backend]||t.backend)+(t.power&&t.power!='on'?' · TV: '+t.power:'');
   $('tFps').textContent=(Number(t.fps)||0).toFixed(1);
   const u=t.ui||{};
   $('tUi').innerHTML=u.running?dot('ok')+esc(u.mode)+(u.alphaSeen?' · Transparenz erkannt':'')+' · '+esc(u.w)+'×'+esc(u.h):dot(u.mode=='off'?'':'warn')+(u.mode=='off'?'aus':'nicht aktiv'+(u.err?': '+esc(u.err):''));
   $('tCov').textContent=Math.round((Number(u.applied)||0)*100)+' % eingemischt · '+Math.round((Number(u.coverage)||0)*100)+' % sichtbar';
   $('tStat').textContent=String(t.status||'');
  }else ['tBack','tFps','tUi','tCov','tStat'].forEach(i=>$(i).textContent='–');
 }catch(e){$('hdr').textContent='keine Verbindung'}
}
async function loadCfg(){
 const r=await fetch('/api/config',{headers:H});if(!r.ok){toast(r.status==401?'Bitte anmelden':'Fehler '+r.status);return}
 const c=await r.json();
 ['pin','leds','order','bri','maxmA','mAch','smooth','idle','idleCol','idleBri','host','allow','type','pinR','pinG','pinB'].forEach(k=>{if(c[k]!==undefined)$(k).value=c[k]});typeUi();
 $('briV').textContent=Math.round(c.bri/2.55)+' %';$('smV').textContent=c.smooth+' %';
 $('adminHint').innerHTML=c.hasAdmin?'Passwort ist gesetzt.':'<span style="color:var(--warn)">Kein Passwort gesetzt – jeder im WLAN kann Einstellungen ändern.</span>';
 $('ssid2').value=c.ssid;
 const tv={},extra=[];
 (c.tv||'').split(',').filter(Boolean).forEach(p=>{const i=p.indexOf('=');const k=p.slice(0,i),v=p.slice(i+1);if(TVKEYS.includes(k))tv[k]=v;else extra.push(p)});
 TVKEYS.forEach(k=>$('t_'+k).value=tv[k]??'');$('t_extra').value=extra.join(',');
}
function typeUi(){const a=$('type').value==='1';$('rgbBox').style.display=a?'':'none';$('pinBox').style.display=a?'none':'';$('orderBox').style.display=a?'none':''}
function saveLed(){const d={};['type','pinR','pinG','pinB','pin','leds','order','bri','maxmA','mAch','smooth','idle','idleCol','idleBri'].forEach(k=>{const v=$(k).value;if(v!=='')d[k]=v});
 post('/api/config',d).then(()=>toast('Gespeichert')).catch(e=>toast(e.message))}
function saveTv(){const parts=[];TVKEYS.forEach(k=>{const v=$('t_'+k).value.trim();if(v!=='')parts.push(k+'='+v)});const ex=$('t_extra').value.trim();if(ex)parts.push(ex);
 post('/api/config',{tv:parts.join(',')}).then(()=>{toast('An Fernseher gesendet');loadCfg()}).catch(e=>toast(e.message))}
function saveSys(){const d={host:$('host').value,allow:$('allow').value};if($('admin').value)d.admin=$('admin').value;
 post('/api/config',d).then(r=>{toast(r.reboot?'Gespeichert – Neustart':'Gespeichert');$('admin').value='';loadCfg()}).catch(e=>toast(e.message))}
function saveWifi(){const s=$('ssidMan').value.trim()||SEL;const a=$('wadmin').value;
 if(!s){toast('Bitte zuerst oben dein WLAN antippen');return}
 if(!$('wpass').value&&!(NETS.find(n=>n.ssid===s)||{}).open&&!confirm('Kein WLAN-Passwort eingegeben. Trotzdem verbinden?'))return;
 if(a.length<6){toast('Bitte ein Admin-Passwort mit mindestens 6 Zeichen festlegen');return}
 post('/api/config',{admin:a,ssid:s,pass:$('wpass').value}).then(()=>toast('Verbinde mit '+s+' … danach http://ambisync.local öffnen')).catch(e=>toast(e.message))}
function saveWifi2(){post('/api/config',{ssid:$('ssid2').value,pass:$('pass2').value||'********'}).then(()=>toast('Neustart …')).catch(e=>toast(e.message))}
function test(m){post('/api/test',{m}).then(()=>toast(m=='none'?'Test beendet':'Testbild läuft')).catch(e=>toast(e.message))}
function upd(){const f=$('fw').files[0];if(!f)return;const fd=new FormData();fd.append('firmware',f);$('updMsg').textContent='lädt hoch …';
 fetch('/update',{method:'POST',headers:H,body:fd}).then(r=>r.text()).then(t=>$('updMsg').textContent=t).catch(()=>$('updMsg').textContent='Fehler')}
let NETS=[],SEL='',scanT=0;
function bars(r){const l=r>-55?4:r>-67?3:r>-77?2:1;return '<span class="bars" title="'+r+' dBm">'+[5,8,11,15].map((h,i)=>'<i style="height:'+h+'px"'+(i<l?' class="on"':'')+'></i>').join('')+'</span>'}
function renderNets(){const box=$('nets');$('netsDl').innerHTML=NETS.map(n=>'<option value="'+esc(n.ssid)+'">').join('');
 if(!NETS.length){box.innerHTML='<div class="hint" style="padding:10px">Keine Netze gefunden – „Neu suchen“ drücken</div>';return}
 box.innerHTML=NETS.map((n,i)=>'<div class="net'+(n.ssid===SEL?' sel':'')+'" data-i="'+i+'">'+bars(n.rssi)+'<span class="n">'+esc(n.ssid)+'</span><small>'+(n.open?'offen':'&#128274;')+' · Kanal '+n.ch+'</small></div>').join('');
 box.querySelectorAll('.net').forEach(d=>d.onclick=()=>pick(NETS[+d.dataset.i]))}
function pick(n){SEL=n.ssid;$('ssidMan').value='';$('pickedName').textContent=n.ssid+(n.open?' (offen)':'');$('picked').style.display='';renderNets();
 const p=$('wpass');p.placeholder=n.open?'offenes Netz – kein Passwort nötig':'Passwort für „'+n.ssid+'“';if(!n.open)p.focus()}
function manual(){const v=$('ssidMan').value.trim();if(v){SEL='';$('pickedName').textContent=v+' (von Hand)';$('picked').style.display='';renderNets()}}
function eye(id,b){const e=$(id);const show=e.type==='password';e.type=show?'text':'password';b.textContent=show?'verbergen':'zeigen'}
let scanBusy=false;
async function scan(force){if(scanBusy)return;scanBusy=true;clearTimeout(scanT);const box=$('nets');
 box.innerHTML='<div class="hint" style="padding:10px">suche WLAN-Netze … (dauert bis zu 5 Sekunden)</div>';
 try{const res=await fetch('/api/scan'+(force?'?refresh=1':''),{headers:H,cache:'no-store'});
  if(!res.ok)throw new Error('Fehler '+res.status+': '+(await res.text()||res.statusText));
  const r=await res.json();NETS=r.list;renderNets();
  if(r.error&&!NETS.length)box.innerHTML='<div class="hint" style="padding:10px">Suche fehlgeschlagen (Code '+r.error+') – siehe Protokoll unten, „Neu suchen“ drücken</div>'}
 catch(e){box.innerHTML='<div class="hint" style="padding:10px">'+esc(e.message||e)+' – „Neu suchen“ drücken</div>'}
 scanBusy=false;loadLog()}
let logOn=false;
async function loadLog(){try{const t=await (await fetch('/api/log',{cache:'no-store'})).text();['log1','log2'].forEach(id=>{const e=$(id);if(!e)return;const end=e.scrollTop+e.clientHeight>=e.scrollHeight-20;e.textContent=t||'(leer)';if(end)e.scrollTop=e.scrollHeight})}catch(e){}}
refresh();loadCfg().then(()=>{});setInterval(refresh,1500);
fetch('/api/status').then(r=>r.json()).then(s=>{if(s.ap){scan(0);setInterval(loadLog,2000)}}).catch(e=>{$('nets').innerHTML='<div class="hint" style="padding:10px">ESP32 antwortet nicht: '+esc(e.message)+'</div>'});
document.querySelectorAll('details').forEach(d=>d.addEventListener('toggle',()=>{if(d.open&&d.querySelector('pre.log'))loadLog()}));
loadLog();
</script></body></html>)HTML";

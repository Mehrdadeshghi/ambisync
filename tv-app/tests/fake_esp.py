# Nachbau der ESP32-Protokollseite (Discovery 21326, Status 21325, Echtzeit 21324) – Logik wie AmbiSync.ino
import socket, json, select, sys, time
def sock(port):
    s=socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR,1); s.bind(("0.0.0.0",port)); return s
disc, stat, rt = sock(21326), sock(21325), sock(21324)
LEDS=123; got={}
t0=time.time()
while time.time()-t0<25 and not ("status" in got and "rt" in got):
    r,_,_=select.select([disc,stat,rt],[],[],0.5)
    for s in r:
        d,a=s.recvfrom(2048)
        if s is disc and d.startswith(b"AMBISYNC-DISCOVER"):
            s.sendto(f"AMBISYNC 1 leds={LEDS} port=21324 fw=1.1.0".encode(), a); got["disc"]=a
        elif s is stat:
            j=json.loads(d.decode()); got["status"]=j
        elif s is rt:
            proto,tmo=d[0],d[1]; leds=(len(d)-2)//3
            got["rt"]=(proto,tmo,leds,list(d[2:2+9]))
print(json.dumps({k:(v if k!="disc" else str(v)) for k,v in got.items()}, ensure_ascii=False))

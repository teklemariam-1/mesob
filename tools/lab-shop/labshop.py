"""Lab stand-in (LAB ONLY: run tools/lab-shop/setup.sh first, then `python -X utf8 tools/lab-shop/labshop.py`)
for the Mesob Shop tablet's Wi-Fi API (same protocol and order rules as ShopServer.kt /
ShopState.kt), so the Windows Player's cart and checkout can be exercised on one PC. Licenses are real:
they are signed by the lab shop key through mesob-lab and sealed to the ordering device.

Cashier actions for testing: POST /lab/paid/<CODE>, POST /lab/cancel/<CODE>, GET /lab/orders.
"""
import base64, json, os, re, secrets, subprocess, sys, threading, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

M = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
HERE = os.path.join(M, "lab", "labshop")  # keys and titles made by setup.sh (git-ignored)
CLI = os.path.join(M, r"target\release\mesob-lab.exe")
ASSETS = os.path.join(M, r"demo\assets")
STUDIO, SHOP, TITLES = (os.path.join(HERE, d) for d in ("studio", "shop", "titles"))

CATALOG = [  # file stem, kind, en, ti, artist, price, poster
    ("starlink", "video", "How Things Click: Starlink", "ስታርሊንክ", "How Things Click", 150, "starlink.jpg"),
    ("asmara-nights", "video", "Asmara Nights", "ለይቲ ኣስመራ", "Studio Seven", 180, "asmara.jpg"),
    ("starlink-audio", "audio", "Starlink (audio)", "ስታርሊንክ", "How Things Click", 30, "starlink.jpg"),
    ("hade-libi", "audio", "Hade Libi", "ሓደ ልቢ", "Studio Seven Band", 40, "hadelibi.jpg"),
    ("proverbs", "pdf", "Tigrinya Proverbs", "ምስላታት ትግርኛ", "Mesob Books", 80, "proverbs.jpg"),
]
TITLE_ID = re.compile(r"title ([0-9a-f]{32})")

def title_id(path):
    out = subprocess.run([CLI, "inspect", "--vendor", os.path.join(M, r"lab\vendor\vendor.pub"), path], capture_output=True, text=True, encoding="utf-8").stdout
    m = re.search(r"([0-9a-f]{32})", out)
    if not m: raise SystemExit(f"can't read title id of {path}: {out}")
    return m.group(1)

titles = {}
for stem, kind, en, ti, artist, price, poster in CATALOG:
    f = os.path.join(TITLES, stem + ".msb")
    tid = title_id(f)
    titles[tid] = dict(file=f, poster=os.path.join(ASSETS, poster), title_id=tid, title_en=en, title_ti=ti, artist=artist, kind=kind, size=os.path.getsize(f), price=price)

LETTERS = "ABCDEFGHJKLMNPQRSTUVWXYZ"
orders, lock = [], threading.Lock()
next_serial = [5000000]

def new_code():
    taken = {o["code"] for o in orders}
    while True:
        c = f"{secrets.choice(LETTERS)}{100 + secrets.randbelow(900)}"
        if c not in taken: return c

def sell(order):
    dev = base64.b64decode(order["device_public"]).hex()
    devf = os.path.join(HERE, "device.pub")
    open(devf, "w").write(dev)
    out = []
    for tid in order["title_ids"]:
        t = titles[tid]
        msl = os.path.join(HERE, "last.msl")
        while True:  # serials used by an earlier run of this lab shop are skipped
            r = subprocess.run([CLI, "sell", "--studio", STUDIO, "--shop", SHOP, "--block", os.path.join(STUDIO, "capacity-1.bin"),
                                "--pack", os.path.join(SHOP, "pack-1.bin"), "--title", t["file"], "--device-pub", devf,
                                "--serial", str(next_serial[0]), "--price", str(t["price"]), "--out", msl], capture_output=True, text=True)
            next_serial[0] += 1
            if r.returncode == 0: break
            if "already used" not in (r.stderr + r.stdout): raise RuntimeError(r.stderr.strip() or r.stdout.strip())
        out.append({"title_id": tid, "bundle": base64.b64encode(open(msl, "rb").read()).decode()})
    return out

class H(BaseHTTPRequestHandler):
    def log_message(self, fmt, *a): sys.stderr.write("%s %s\n" % (self.command, self.path))

    def send_json(self, obj, code=200):
        b = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(code); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)

    def do_GET(self):
        p = self.path.rstrip("/")
        if p == "/api/catalog":
            return self.send_json({"shop_id": 4, "shop_name": "Harnet Avenue Media", "titles": [{k: v for k, v in t.items() if k not in ("file", "poster")} for t in titles.values()]})
        if p.startswith("/api/poster/"):
            t = titles.get(p.rsplit("/", 1)[1])
            if not t: return self.send_json({"error": "no poster"}, 404)
            b = open(t["poster"], "rb").read()
            self.send_response(200); self.send_header("Content-Type", "image/jpeg"); self.send_header("Content-Length", str(len(b))); self.end_headers(); return self.wfile.write(b)
        if p.startswith("/api/orders/"):
            tok = p.rsplit("/", 1)[1]
            with lock: o = next((o for o in orders if re.fullmatch(r"[0-9a-f]{48}", tok) and o["token"] == tok), None)
            if not o: return self.send_json({"error": "no such order"}, 404)
            return self.send_json({"code": o["code"], "status": o["status"], "total": o["total"], "licenses": o["licenses"]})
        if p.startswith("/api/titles/"):
            t = titles.get(p.rsplit("/", 1)[1])
            if not t: return self.send_json({"error": "not in this shop"}, 404)
            size = t["size"]; start = 0
            m = re.match(r"bytes=(\d+)-", self.headers.get("Range", ""))
            if m and 0 < int(m.group(1)) < size: start = int(m.group(1))
            self.send_response(206 if start else 200)
            self.send_header("Content-Type", "application/octet-stream"); self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Length", str(size - start))
            if start: self.send_header("Content-Range", f"bytes {start}-{size - 1}/{size}")
            self.end_headers()
            with open(t["file"], "rb") as f:
                f.seek(start)
                while chunk := f.read(1 << 16): self.wfile.write(chunk)
            return
        if p == "/lab/orders":
            with lock: return self.send_json([{k: o[k] for k in ("code", "status", "total", "title_ids")} for o in orders])
        self.send_json({"error": "not found"}, 404)

    def do_POST(self):
        p = self.path.rstrip("/")
        body = self.rfile.read(int(self.headers.get("Content-Length", 0) or 0))
        if p == "/api/orders":
            req = json.loads(body or b"{}")
            ids = list(dict.fromkeys(req.get("title_ids", [])))
            if not 1 <= len(ids) <= 20: return self.send_json({"error": "1 to 20 titles per order"}, 400)
            if any(i not in titles for i in ids): return self.send_json({"error": "unknown title"}, 400)
            with lock:
                for o in orders:
                    if req.get("replaces") and o["token"] == req["replaces"] and o["status"] == "pending": o["status"] = "replaced"
                if sum(o["status"] == "pending" for o in orders) >= 30: return self.send_json({"error": "Too many open orders. Ask at the counter."}, 400)
                o = dict(code=new_code(), token=secrets.token_hex(24), device_public=req["device_public"], title_ids=ids,
                         total=sum(titles[i]["price"] for i in ids), status="pending", licenses=[], created=time.time())
                orders.append(o)
            print(f"NEW ORDER {o['code']} total {o['total']} ({len(ids)} titles)", flush=True)
            return self.send_json({"code": o["code"], "token": o["token"], "total": o["total"],
                                   "items": [{"title_id": i, "title_en": titles[i]["title_en"], "price": titles[i]["price"]} for i in ids]})
        m = re.fullmatch(r"/lab/(paid|cancel)/([A-Za-z0-9]+)", p)
        if m:
            with lock:
                o = next((o for o in reversed(orders) if o["code"] == m.group(2).upper()), None)
                if not o or o["status"] != "pending": return self.send_json({"error": "no pending order with that code"}, 404)
                if m.group(1) == "paid":
                    try: o["licenses"] = sell(o)
                    except Exception as e: return self.send_json({"error": str(e)}, 500)
                    o["status"] = "paid"
                else: o["status"] = "cancelled"
            return self.send_json({"code": o["code"], "status": o["status"]})
        self.send_json({"error": "not found"}, 404)

print(f"lab shop: {len(titles)} titles on http://127.0.0.1:8765", flush=True)
ThreadingHTTPServer(("127.0.0.1", 8765), H).serve_forever()

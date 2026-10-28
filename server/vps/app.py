"""Nowcast cover storage. Only nginx may reach the loopback HTTP listener."""
import hashlib
import hmac
import html
import io
import json
import os
import re
import secrets
import sqlite3
import threading
import time
import warnings
from contextlib import contextmanager
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit
from zoneinfo import ZoneInfo

from PIL import Image

MAX_SIZE = 256 * 1024
KEEP = 6 * 3600
COVER_KEEP = 3600
DB = os.environ.get('NOWCAST_DB', '/var/lib/nowcast-covers/covers.db')
ORIGIN = os.environ.get('PUBLIC_ORIGIN', 'https://nowcast.85-192-42-111.sslip.io:10443')
ADMIN_KEY = os.environ.get('ADMIN_KEY', '')
BRIDGE_KEY = os.environ.get('BRIDGE_KEY', '')
IP_SALT = os.environ.get('IP_SALT', '')
Image.MAX_IMAGE_PIXELS = 16_000_000


@contextmanager
def connect():
    db = sqlite3.connect(DB, timeout=15)
    db.row_factory = sqlite3.Row
    try:
        with db:
            yield db
    finally:
        db.close()


def setup():
    with connect() as db:
        db.executescript('''
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS pictures (
          id TEXT PRIMARY KEY, type TEXT NOT NULL, data BLOB NOT NULL,
          added INTEGER NOT NULL, kind TEXT NOT NULL, source TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS uploaders (
          id TEXT NOT NULL, ip TEXT NOT NULL, at INTEGER NOT NULL,
          PRIMARY KEY(id, ip));
        CREATE TABLE IF NOT EXISTS uploads (ip TEXT, at INTEGER);
        CREATE INDEX IF NOT EXISTS uploads_ip_at ON uploads(ip, at);
        CREATE INDEX IF NOT EXISTS pictures_added ON pictures(added);
        CREATE TABLE IF NOT EXISTS blocked (id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS banned (ip TEXT PRIMARY KEY);
        ''')
        db.execute('DELETE FROM uploaders WHERE id NOT IN (SELECT id FROM pictures)')


def cleanup():
    with connect() as db:
        now = int(time.time())
        db.execute("DELETE FROM pictures WHERE (kind='own' AND added<=?) OR (kind='cover' AND added<=?)",
                   (now-KEEP,now-COVER_KEEP))
        db.execute('DELETE FROM uploaders WHERE id NOT IN (SELECT id FROM pictures)')
        db.execute('DELETE FROM uploads WHERE at < ?', (int(time.time()) - 3600,))


def store(body, ip, kind='cover', source='vps'):
    if kind not in ('own','cover'):
        return 400, 'Invalid picture kind'
    if not body or len(body) > MAX_SIZE:
        return 413, 'The picture must be up to 256 KB'
    try:
        with warnings.catch_warnings():
            warnings.simplefilter('error', Image.DecompressionBombWarning)
            image = Image.open(io.BytesIO(body))
            fmt = image.format
            if fmt not in ('JPEG', 'PNG'):
                return 415, 'Only JPEG or PNG'
            image.verify()
            with Image.open(io.BytesIO(body)) as decoded:
                decoded.load()
    except Exception:
        return 415, 'Invalid JPEG or PNG'
    ext = 'png' if fmt == 'PNG' else 'jpg'
    pic = hashlib.sha1(body).hexdigest()
    address = hmac.new(IP_SALT.encode(), ip.encode(), hashlib.sha256).hexdigest()
    now = int(time.time())
    with connect() as db:
        # One write transaction makes the rate check and insert atomic.
        db.execute('BEGIN IMMEDIATE')
        if db.execute('SELECT 1 FROM banned WHERE ip=?', (address,)).fetchone():
            return 403, 'Banned'
        if db.execute('SELECT 1 FROM blocked WHERE id=?', (pic,)).fetchone():
            return 451, 'Blocked'
        count = db.execute('SELECT COUNT(*) FROM uploads WHERE ip=? AND at>?', (address, now-3600)).fetchone()[0]
        if count >= 120:
            return 429, 'Too many uploads, try again later'
        total = db.execute('SELECT COALESCE(SUM(length(data)),0) FROM pictures').fetchone()[0]
        exists = db.execute('SELECT 1 FROM pictures WHERE id=?', (pic,)).fetchone()
        if not exists and total + len(body) > 5 * 1024**3:
            return 507, 'Storage full, try again later'
        db.execute('''INSERT INTO pictures VALUES (?,?,?,?,?,?)
          ON CONFLICT(id) DO UPDATE SET added=excluded.added,
          kind=CASE WHEN pictures.kind='own' OR excluded.kind='own' THEN 'own' ELSE 'cover' END,
          source=CASE WHEN pictures.source<>excluded.source THEN 'both' ELSE pictures.source END''',
                   (pic, ext, body, now, kind, source))
        db.execute('INSERT INTO uploaders VALUES (?,?,?) ON CONFLICT(id,ip) DO UPDATE SET at=excluded.at', (pic,address,now))
        db.execute('INSERT INTO uploads VALUES (?,?)', (address,now))
    return 200, ORIGIN + '/' + pic + '.' + ext


STYLE = '''<style>
:root{color-scheme:dark}body{font:16px system-ui;background:#313338;color:#eee;max-width:1150px;margin:auto;padding:24px}
a{color:#aebaff}button,input,select{font:inherit;padding:9px;border-radius:6px;border:1px solid #555;background:#232428;color:white}
button{cursor:pointer}button:hover{background:#4752c4}.ban{background:#9c3035}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:16px}
.card{background:#232428;border-radius:10px;padding:12px;text-align:center}.card img{width:100%;height:180px;object-fit:contain;background:#1e1f22;border-radius:6px}
.actions{display:flex;gap:8px;justify-content:center;margin-top:12px}small{display:block;color:#aaa;margin:8px 0}nav{margin:20px 0;display:flex;gap:16px;flex-wrap:wrap}
.note{padding:12px;background:#232428;border-radius:8px}input[type=password]{width:min(80%,360px)}</style>'''


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        # Never log keys, cookies or query strings.
        print('%s %s %s' % (self.command, urlsplit(self.path).path, args[1] if len(args)>1 else ''), flush=True)

    def send(self, status, body, mime='text/plain; charset=utf-8', headers=None):
        data = body.encode() if isinstance(body,str) else body
        self.send_response(status)
        for k,v in {'Content-Type':mime,'Content-Length':str(len(data)),
                    'Cache-Control':'no-store','X-Content-Type-Options':'nosniff',
                    'Referrer-Policy':'no-referrer','X-Frame-Options':'DENY', **(headers or {})}.items():
            self.send_header(k,v)
        self.end_headers()
        if self.command != 'HEAD':
            self.wfile.write(data)

    def read(self, maximum):
        try:
            n = int(self.headers.get('Content-Length','-1'))
        except ValueError:
            n = -1
        if n < 0 or n > maximum:
            return None
        self.connection.settimeout(15)
        body = self.rfile.read(n)
        return body if len(body)==n else None

    def bridge(self):
        return bool(BRIDGE_KEY) and hmac.compare_digest(self.headers.get('Authorization',''), 'Bearer '+BRIDGE_KEY)

    def cookie(self, name):
        for part in self.headers.get('Cookie','').split(';'):
            key,_,value = part.strip().partition('=')
            if key == name:
                return value
        return ''

    def authenticated(self):
        token = self.cookie('nowcast_admin')
        stamp,_,signature = token.partition('.')
        try:
            valid_time = 0 <= time.time()-int(stamp) < 12*3600
        except ValueError:
            return False
        expected = hmac.new(ADMIN_KEY.encode(),stamp.encode(),hashlib.sha256).hexdigest()
        return bool(ADMIN_KEY) and valid_time and hmac.compare_digest(signature,expected)

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        url = urlsplit(self.path)
        query = parse_qs(url.query)
        if url.path == '/health':
            with connect() as db:
                db.execute('SELECT 1').fetchone()
            return self.send(200,'ok')
        if url.path == '/bridge/check':
            if not self.bridge():
                return self.send(404,'Not found')
            pic = query.get('id',[''])[0]
            with connect() as db:
                blocked = bool(db.execute('SELECT 1 FROM blocked WHERE id=?',(pic,)).fetchone())
            return self.send(200,json.dumps({'blocked':blocked}),'application/json')
        match = re.fullmatch(r'/([a-f0-9]{40})\.(jpg|png)',url.path)
        if match:
            with connect() as db:
                now = int(time.time())
                row = db.execute("SELECT * FROM pictures WHERE id=? AND type=? AND ((kind='own' AND added>?) OR (kind='cover' AND added>?))",
                                 (match[1],match[2],now-KEEP,now-COVER_KEEP)).fetchone()
            if not row:
                return self.send(404,'Not found')
            return self.send(200,row['data'],'image/png' if row['type']=='png' else 'image/jpeg')
        if url.path == '/admin':
            key = query.get('key',[''])[0]
            if key and ADMIN_KEY and hmac.compare_digest(key,ADMIN_KEY):
                return self.login()
            if not self.authenticated():
                return self.send(200,'<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Nowcast — вход</title>'+STYLE+
                                 '<h1>Nowcast</h1><p>Вход в модерацию обложек</p><form method="post" action="/login"><input name="key" type="password" placeholder="Ключ администратора" required><button>Войти</button></form>','text/html; charset=utf-8')
            return self.admin(query)
        if url.path == '/':
            page = open(os.path.join(os.path.dirname(__file__),'test.html'),encoding='utf-8').read()
            return self.send(200,page,'text/html; charset=utf-8')
        return self.send(404,'Not found')

    def login(self):
        stamp = str(int(time.time()))
        token = stamp+'.'+hmac.new(ADMIN_KEY.encode(),stamp.encode(),hashlib.sha256).hexdigest()
        return self.send(303,'',headers={'Location':'/admin','Set-Cookie':'nowcast_admin='+token+'; HttpOnly; Secure; SameSite=Strict; Path=/; Max-Age=43200'})

    def do_POST(self):
        url = urlsplit(self.path)
        if url.path in ('/upload','/bridge/upload'):
            if url.path.startswith('/bridge') and not self.bridge():
                return self.send(404,'Not found')
            body = self.read(MAX_SIZE)
            if body is None:
                return self.send(413,'The picture must be up to 256 KB')
            # nginx overwrites X-Real-IP; arbitrary forwarded headers are ignored.
            ip = self.headers.get('X-Real-IP',self.client_address[0])
            source = 'vps'
            if url.path.startswith('/bridge'):
                ip = self.headers.get('X-Nowcast-IP','')
                source = 'cloudflare'
                if not ip:
                    return self.send(400,'Missing address')
            kind = 'own' if parse_qs(url.query).get('kind',[''])[0]=='own' else 'cover'
            status,answer = store(body,ip,kind,source)
            return self.send(status,answer)
        if url.path not in ('/login','/admin','/logout'):
            return self.send(404,'Not found')
        body = self.read(8192)
        if body is None:
            return self.send(413,'Too large')
        form = parse_qs(body.decode('utf-8',errors='replace'))
        if url.path == '/login':
            key = form.get('key',[''])[0]
            if ADMIN_KEY and hmac.compare_digest(key,ADMIN_KEY):
                return self.login()
            return self.send(403,'Invalid key')
        if not self.authenticated():
            return self.send(403,'Sign in first')
        csrf = form.get('csrf',[''])[0]
        expected = hmac.new(ADMIN_KEY.encode(),self.cookie('nowcast_admin').encode(),hashlib.sha256).hexdigest()
        if not hmac.compare_digest(csrf,expected):
            return self.send(403,'Invalid form')
        if url.path == '/logout':
            return self.send(303,'',headers={'Location':'/admin','Set-Cookie':'nowcast_admin=; Secure; HttpOnly; SameSite=Strict; Path=/; Max-Age=0'})
        pic = form.get('id',[''])[0].strip().split('/')[-1].split('.')[0]
        action = form.get('action',['delete'])[0]
        if not re.fullmatch('[a-f0-9]{40}',pic) or action not in ('delete','ban'):
            return self.send(400,'Invalid action')
        with connect() as db:
            db.execute('BEGIN IMMEDIATE')
            if action == 'ban':
                # Ban all recorded uploaders, not whichever last overwrote a row.
                db.execute('INSERT OR IGNORE INTO banned SELECT ip FROM uploaders WHERE id=?',(pic,))
            db.execute('INSERT OR IGNORE INTO blocked VALUES (?)',(pic,))
            db.execute('DELETE FROM pictures WHERE id=?',(pic,))
        return self.send(303,'',headers={'Location':'/admin?done='+action})

    def admin(self, query):
        cleanup()
        try:
            page = max(0,min(100000,int(query.get('page',['0'])[0])))
        except ValueError:
            page = 0
        kind = 'own'
        where,params = ' WHERE kind=?',('own',)
        with connect() as db:
            total = db.execute('SELECT COUNT(*) FROM pictures'+where,params).fetchone()[0]
            rows = db.execute('SELECT id,type,kind,source,added FROM pictures'+where+' ORDER BY added DESC LIMIT 60 OFFSET ?',params+(page*60,)).fetchall()
            banned = db.execute('SELECT COUNT(*) FROM banned').fetchone()[0]
        csrf = hmac.new(ADMIN_KEY.encode(),self.cookie('nowcast_admin').encode(),hashlib.sha256).hexdigest()
        cards = []
        for r in rows:
            date = datetime.fromtimestamp(r['added'],ZoneInfo('Europe/Moscow')).strftime('%d.%m %H:%M')
            image = '/'+r['id']+'.'+r['type']
            label = 'Выбрано пользователем'
            cards.append(f'''<article class="card"><a href="{image}" target="_blank" rel="noreferrer"><img loading="lazy" src="{image}" alt="{label}"></a>
            <small>{label} · {date} МСК<br>{html.escape(r['source'])}</small><form method="post" action="/admin">
            <input type="hidden" name="csrf" value="{csrf}"><input type="hidden" name="id" value="{r['id']}">
            <div class="actions"><button name="action" value="delete">Удалить</button><button class="ban" name="action" value="ban">Бан</button></div></form></article>''')
        nav = '<nav><a href="/admin">Обновить</a></nav>'
        pager = (f'<a href="?kind={kind}&page={page-1}">← Назад</a> ' if page else '')
        if (page+1)*60 < total:
            pager += f'<a href="?kind={kind}&page={page+1}">Дальше →</a>'
        message = '<p class="note">Картинка удалена'+(' и загрузчик забанен' if query.get('done')==['ban'] else '')+'.</p>' if 'done' in query else ''
        page_html = f'''<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Nowcast — обложки</title>{STYLE}
        <h1>Картинки пользователей</h1><p>Только выбранные вручную картинки · Картинок: {total} · Забаненных адресов: {banned} · Хранение: 6 часов после последней загрузки</p>
        <p>«Удалить» запрещает эту картинку. «Бан» также запрещает её загрузчикам новые загрузки.</p>{message}{nav}
        <div class="grid">{''.join(cards) or '<p>Пока нет картинок.</p>'}</div><nav>{pager}</nav>
        <form method="post" action="/logout"><input type="hidden" name="csrf" value="{csrf}"><button>Выйти</button></form></html>'''
        return self.send(200,page_html,'text/html; charset=utf-8')


if __name__ == '__main__':
    if not ADMIN_KEY or not BRIDGE_KEY or not IP_SALT:
        raise SystemExit('ADMIN_KEY, BRIDGE_KEY and IP_SALT must be configured')
    setup()
    def sweep():
        while True:
            try:
                cleanup()
            except Exception as e:
                print('Cleanup failed: '+type(e).__name__,flush=True)
            time.sleep(60)
    threading.Thread(target=sweep,daemon=True).start()
    ThreadingHTTPServer(('127.0.0.1',18761),Handler).serve_forever()

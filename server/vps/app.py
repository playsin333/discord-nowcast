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
        protect_existing = not db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='protected_pictures'").fetchone()
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
        CREATE TABLE IF NOT EXISTS statuses (
          id TEXT PRIMARY KEY, ip TEXT NOT NULL, title TEXT NOT NULL,
          line2 TEXT NOT NULL, type TEXT NOT NULL, picture TEXT NOT NULL, added INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS statuses_added ON statuses(added);
        CREATE TABLE IF NOT EXISTS blocked_statuses (id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS picture_slots (
          client TEXT NOT NULL, slot TEXT NOT NULL, picture TEXT NOT NULL, at INTEGER NOT NULL,
          PRIMARY KEY(client,slot));
        CREATE INDEX IF NOT EXISTS picture_slots_picture ON picture_slots(picture);
        CREATE TABLE IF NOT EXISTS protected_pictures (id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS blocked (id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS banned (ip TEXT PRIMARY KEY);
        ''')
        columns = {row['name'] for row in db.execute('PRAGMA table_info(statuses)')}
        if 'client' not in columns:
            db.execute("ALTER TABLE statuses ADD COLUMN client TEXT NOT NULL DEFAULT ''")
        db.execute("CREATE UNIQUE INDEX IF NOT EXISTS statuses_client ON statuses(client) WHERE client<>''")
        if protect_existing:
            db.execute('INSERT OR IGNORE INTO protected_pictures SELECT id FROM pictures')
        db.execute('DELETE FROM uploaders WHERE id NOT IN (SELECT id FROM pictures)')


def cleanup():
    with connect() as db:
        now = int(time.time())
        db.execute("DELETE FROM pictures WHERE (kind='own' AND added<=?) OR (kind='cover' AND added<=?)",
                   (now-KEEP,now-COVER_KEEP))
        db.execute('DELETE FROM uploaders WHERE id NOT IN (SELECT id FROM pictures)')
        db.execute('DELETE FROM statuses WHERE added<=?', (now-KEEP,))
        db.execute('DELETE FROM picture_slots WHERE at<=?', (now-KEEP,))
        db.execute('DELETE FROM protected_pictures WHERE id NOT IN (SELECT id FROM pictures)')
        db.execute('DELETE FROM uploads WHERE at < ?', (int(time.time()) - 3600,))


def installation(token):
    if not token:
        return ''
    if not isinstance(token,str) or not re.fullmatch(r'[a-f0-9]{64}',token):
        raise ValueError('Invalid installation')
    return hashlib.sha256(token.encode()).hexdigest()


def discard_picture(db, picture):
    pic = picture.split('.')[0]
    # Only delete tracked files. Old clients and ordinary player covers keep
    # their own expiry; another installation or slot still using it wins.
    if not pic or db.execute('SELECT 1 FROM protected_pictures WHERE id=?',(pic,)).fetchone():
        return
    if db.execute('SELECT 1 FROM picture_slots WHERE picture=?',(picture,)).fetchone():
        return
    if db.execute('SELECT 1 FROM statuses WHERE picture=?',(picture,)).fetchone():
        return
    db.execute("DELETE FROM pictures WHERE id=? AND kind='own'",(pic,))
    db.execute('DELETE FROM uploaders WHERE id=? AND NOT EXISTS (SELECT 1 FROM pictures WHERE id=?)',(pic,pic))


def store(body, ip, kind='cover', source='vps', token='', slot=''):
    try:
        client = installation(token)
        if (client and (slot not in ('status','fallback') or kind!='own')) or (slot and not client):
            raise ValueError()
    except ValueError:
        return 400, 'Invalid picture owner'

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
        if client:
            previous = db.execute('SELECT picture FROM picture_slots WHERE client=? AND slot=?',(client,slot)).fetchone()
            db.execute('INSERT INTO picture_slots VALUES (?,?,?,?) ON CONFLICT(client,slot) DO UPDATE SET picture=excluded.picture,at=excluded.at',
                       (client,slot,pic+'.'+ext,now))
            if previous and previous['picture'] != pic+'.'+ext:
                discard_picture(db,previous['picture'])
        else:
            db.execute('INSERT OR IGNORE INTO protected_pictures VALUES (?)',(pic,))
        db.execute('INSERT INTO uploads VALUES (?,?)', (address,now))
    return 200, ORIGIN + '/' + pic + '.' + ext


def store_status(body, ip):
    try:
        data = json.loads(body)
        if not isinstance(data, dict):
            raise ValueError()
        title, line2, kind, picture = (data.get(k, '') for k in ('title', 'line2', 'type', 'picture'))
        if not all(isinstance(v, str) for v in (title, line2, kind, picture)):
            raise ValueError()
        client = installation(data.get('client',''))
        title, line2 = title.strip(), line2.strip()
        if not title or len(title)>128 or len(line2)>128 or kind not in ('listening','watching','playing'):
            raise ValueError()
        # Store only a picture id; arbitrary external URLs are never rendered.
        if picture and not re.fullmatch(r'[a-f0-9]{40}\.(jpg|png)', picture):
            raise ValueError()
    except (ValueError, UnicodeError):
        return 400, 'Invalid status'
    address = hmac.new(IP_SALT.encode(), ip.encode(), hashlib.sha256).hexdigest()
    identity = json.dumps([client or address,title,line2,kind,picture],ensure_ascii=True)
    status_id = hashlib.sha256(identity.encode()).hexdigest()
    now = int(time.time())
    with connect() as db:
        db.execute('BEGIN IMMEDIATE')
        if db.execute('SELECT 1 FROM banned WHERE ip=?',(address,)).fetchone():
            return 403, 'Banned'
        if db.execute('SELECT 1 FROM blocked_statuses WHERE id=?',(status_id,)).fetchone():
            return 451, 'Blocked'
        if picture:
            pic = picture.split('.')[0]
            if db.execute('SELECT 1 FROM blocked WHERE id=?',(pic,)).fetchone():
                return 451, 'Blocked'
            # Same picture alone is not proof of ownership of another user's upload.
            owned = db.execute('SELECT 1 FROM picture_slots WHERE client=? AND slot=? AND picture=?',(client,'status',picture)).fetchone() if client else db.execute('SELECT 1 FROM uploaders WHERE id=? AND ip=?',(pic,address)).fetchone()
            if not owned:
                return 400, 'Upload picture first'
        count = db.execute('SELECT COUNT(*) FROM uploads WHERE ip=? AND at>?',(address,now-3600)).fetchone()[0]
        if count>=120:
            return 429, 'Too many updates, try again later'
        previous = db.execute("SELECT picture FROM statuses WHERE client=? AND client<>''",(client,)).fetchone()
        if client:
            db.execute('DELETE FROM statuses WHERE client=?',(client,))
        db.execute('INSERT INTO statuses (id,ip,title,line2,type,picture,added,client) VALUES (?,?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET added=excluded.added,ip=excluded.ip',
                   (status_id,address,title,line2,kind,picture,now,client))
        if client:
            previous_slot = db.execute("SELECT picture FROM picture_slots WHERE client=? AND slot='status'",(client,)).fetchone()
            if not picture:
                db.execute("DELETE FROM picture_slots WHERE client=? AND slot='status'",(client,))
            if previous_slot and previous_slot['picture'] != picture:
                discard_picture(db,previous_slot['picture'])
            if previous and previous['picture'] != picture:
                discard_picture(db,previous['picture'])
        db.execute('INSERT INTO uploads VALUES (?,?)',(address,now))
    return 200, 'ok'


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
        if url.path == '/status':
            body = self.read(4096)
            if body is None:
                return self.send(413,'Too large')
            ip = self.headers.get('X-Real-IP',self.client_address[0])
            status,answer = store_status(body,ip)
            return self.send(status,answer)
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
            token = self.headers.get('X-Nowcast-Installation','') if source=='vps' else ''
            slot = self.headers.get('X-Nowcast-Slot','') if source=='vps' else ''
            status,answer = store(body,ip,kind,source,token,slot)
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
        status_id = form.get('status_id',[''])[0]
        if status_id:
            action = form.get('action',['delete'])[0]
            if not re.fullmatch('[a-f0-9]{64}',status_id) or action not in ('delete','ban'):
                return self.send(400,'Invalid action')
            with connect() as db:
                db.execute('BEGIN IMMEDIATE')
                if action == 'ban':
                    db.execute('INSERT OR IGNORE INTO banned SELECT ip FROM statuses WHERE id=?',(status_id,))
                db.execute('INSERT OR IGNORE INTO blocked_statuses VALUES (?)',(status_id,))
                previous = db.execute('SELECT client,picture FROM statuses WHERE id=?',(status_id,)).fetchone()
                db.execute('DELETE FROM statuses WHERE id=?',(status_id,))
                if previous and previous['client']:
                    db.execute("DELETE FROM picture_slots WHERE client=? AND slot='status' AND picture=?",(previous['client'],previous['picture']))
                    discard_picture(db,previous['picture'])
            return self.send(303,'',headers={'Location':'/admin?done='+action})
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
            db.execute('INSERT OR IGNORE INTO blocked_statuses SELECT id FROM statuses WHERE picture LIKE ?',(pic+'.%',))
            db.execute('DELETE FROM statuses WHERE picture LIKE ?',(pic+'.%',))
            db.execute('DELETE FROM picture_slots WHERE picture LIKE ?',(pic+'.%',))
            db.execute('DELETE FROM pictures WHERE id=?',(pic,))
        return self.send(303,'',headers={'Location':'/admin?done='+action})

    def admin(self, query):
        cleanup()
        try:
            page = max(0,min(100000,int(query.get('page',['0'])[0])))
        except ValueError:
            page = 0
        kind = 'own'
        where,params = " WHERE kind=? AND NOT EXISTS (SELECT 1 FROM statuses WHERE statuses.picture=pictures.id||'.'||pictures.type) AND NOT EXISTS (SELECT 1 FROM picture_slots WHERE slot='status' AND picture= pictures.id||'.'||pictures.type)",('own',)
        with connect() as db:
            total = db.execute('SELECT COUNT(*) FROM pictures'+where,params).fetchone()[0]
            rows = db.execute('SELECT id,type,kind,source,added FROM pictures'+where+' ORDER BY added DESC LIMIT 60 OFFSET ?',params+(page*60,)).fetchall()
            statuses = db.execute('SELECT * FROM statuses ORDER BY added DESC LIMIT 60').fetchall()
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
        status_cards = []
        for r in statuses:
            date = datetime.fromtimestamp(r['added'],ZoneInfo('Europe/Moscow')).strftime('%d.%m %H:%M')
            image = '<img loading="lazy" src="/'+r['picture']+'" alt="Картинка статуса">' if r['picture'] else '<p>Без картинки</p>'
            status_cards.append(f'''<article class="card">{image}
            <div style="text-align:left;overflow-wrap:anywhere;white-space:pre-wrap"><strong>{html.escape(r['title'])}</strong><br>{html.escape(r['line2'])}</div>
            <small>{html.escape(r['type'])} · {date} МСК</small>
            <form method="post" action="/admin"><input type="hidden" name="csrf" value="{csrf}"><input type="hidden" name="status_id" value="{r['id']}">
            <div class="actions"><button name="action" value="delete">Удалить</button><button class="ban" name="action" value="ban">Бан</button></div></form></article>''')
        nav = '<nav><a href="/admin">Обновить</a></nav>'
        pager = (f'<a href="?kind={kind}&page={page-1}">← Назад</a> ' if page else '')
        if (page+1)*60 < total:
            pager += f'<a href="?kind={kind}&page={page+1}">Дальше →</a>'
        message = '<p class="note">Картинка удалена'+(' и загрузчик забанен' if query.get('done')==['ban'] else '')+'.</p>' if 'done' in query else ''
        page_html = f'''<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Nowcast — обложки</title>{STYLE}
        <h1>Картинки пользователей</h1><p>Только выбранные вручную картинки · Картинок: {total} · Забаненных адресов: {banned} · Хранение: 6 часов после последней загрузки</p>
        <p>«Удалить» запрещает эту картинку. «Бан» также запрещает её загрузчикам новые загрузки.</p>{message}{nav}
        <h2>Тексты кастомных статусов</h2><p>Последние 60 записей, хранение 6 часов. Удаление запрещает повторную отправку этой записи; бан блокирует новые отправки с адреса.</p>
        <div class="grid">{''.join(status_cards) or '<p>Пока нет текстов. Нужна Windows-версия 1.5.4 или новее.</p>'}</div>
        <h2>Выбранные картинки</h2><div class="grid">{''.join(cards) or '<p>Пока нет картинок.</p>'}</div><nav>{pager}</nav>
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

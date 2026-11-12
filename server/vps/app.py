"""Nowcast cover storage. Only nginx may reach the loopback HTTP listener."""
import hashlib
import base64
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
from urllib.parse import parse_qs, urlsplit, urlencode
from zoneinfo import ZoneInfo

from PIL import Image

MAX_STATIC_SIZE = 256 * 1024
MAX_SIZE = 2 * 1024 * 1024
GIF_MAX_SIDE = 512
GIF_MAX_FRAMES = 200
GIF_MAX_PIXELS = 40_000_000
GIF_LIMITS = 'GIF: up to 2 MiB, 512 x 512, 200 frames and 40 million total frame pixels'
MIME_TYPES = {'png': 'image/png', 'jpg': 'image/jpeg', 'gif': 'image/gif'}
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
        migrate_owners = not db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='picture_clients'").fetchone()
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
        CREATE TABLE IF NOT EXISTS picture_slots (
          client TEXT NOT NULL, slot TEXT NOT NULL, picture TEXT NOT NULL, at INTEGER NOT NULL,
          PRIMARY KEY(client,slot));
        CREATE INDEX IF NOT EXISTS picture_slots_picture ON picture_slots(picture);
        CREATE TABLE IF NOT EXISTS protected_pictures (id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS blocked (id TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS banned (ip TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS picture_clients (
          id TEXT NOT NULL, client TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(id,client));
        CREATE INDEX IF NOT EXISTS picture_clients_client ON picture_clients(client);
        CREATE TABLE IF NOT EXISTS legacy_uploaders (
          id TEXT NOT NULL, ip TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(id,ip));
        CREATE TABLE IF NOT EXISTS installation_bans (
          client TEXT PRIMARY KEY, at INTEGER NOT NULL, picture TEXT NOT NULL, reason TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS legacy_ban_meta (
          ip TEXT PRIMARY KEY, at INTEGER NOT NULL, picture TEXT NOT NULL, reason TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS blocked_meta (
          id TEXT PRIMARY KEY, at INTEGER NOT NULL, reason TEXT NOT NULL, thumb BLOB);
        CREATE TABLE IF NOT EXISTS moderation_events (
          seq INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, action TEXT NOT NULL,
          target_type TEXT NOT NULL, target TEXT NOT NULL, picture TEXT NOT NULL,
          reason TEXT NOT NULL, actor TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS client_uploads (client TEXT NOT NULL, at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS client_uploads_at ON client_uploads(client,at);
        ''')
        # Text moderation is retired; purge historical text on upgrade.
        db.execute('DROP TABLE IF EXISTS statuses')
        db.execute('DROP TABLE IF EXISTS blocked_statuses')
        if protect_existing:
            db.execute('INSERT OR IGNORE INTO protected_pictures SELECT id FROM pictures')
        db.execute('DELETE FROM uploaders WHERE id NOT IN (SELECT id FROM pictures)')
        db.execute("INSERT OR IGNORE INTO picture_clients SELECT substr(picture,1,40),client,at FROM picture_slots WHERE substr(picture,1,40) IN (SELECT id FROM pictures)")
        if migrate_owners:
            # Historical IP uploads cannot be assigned to an installation reliably.
            db.execute("INSERT OR IGNORE INTO legacy_uploaders SELECT * FROM uploaders WHERE id IN (SELECT id FROM protected_pictures) OR id NOT IN (SELECT id FROM picture_clients)")
        db.execute("INSERT OR IGNORE INTO blocked_meta SELECT id,0,'Старый запрет картинки',NULL FROM blocked")
        db.execute("INSERT OR IGNORE INTO legacy_ban_meta SELECT ip,0,'','Старый бан по IP' FROM banned")


def cleanup():
    with connect() as db:
        now = int(time.time())
        db.execute("DELETE FROM pictures WHERE (kind='own' AND added<=?) OR (kind='cover' AND added<=?)",
                   (now-KEEP,now-COVER_KEEP))
        db.execute('DELETE FROM uploaders WHERE id NOT IN (SELECT id FROM pictures)')
        db.execute('DELETE FROM picture_slots WHERE at<=?', (now-KEEP,))
        db.execute('DELETE FROM picture_clients WHERE id NOT IN (SELECT id FROM pictures)')
        db.execute('DELETE FROM legacy_uploaders WHERE id NOT IN (SELECT id FROM pictures)')
        db.execute('DELETE FROM client_uploads WHERE at<?', (now-3600,))
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
    db.execute("DELETE FROM pictures WHERE id=? AND kind='own'",(pic,))
    for table in ('uploaders','picture_clients','legacy_uploaders'):
        db.execute('DELETE FROM '+table+' WHERE id=? AND NOT EXISTS (SELECT 1 FROM pictures WHERE id=?)',(pic,pic))


def store(body, ip, kind='cover', source='vps', token='', slot=''):
    try:
        client = installation(token)
        if (client and kind=='own' and slot not in ('status','fallback')) or (slot and (not client or kind!='own')):
            raise ValueError()
    except ValueError:
        return 400, 'Invalid picture owner'

    if kind not in ('own','cover'):
        return 400, 'Invalid picture kind'
    if not body or len(body) > MAX_SIZE:
        return 413, 'The picture must be up to 2 MiB'
    try:
        with warnings.catch_warnings():
            warnings.simplefilter('error', Image.DecompressionBombWarning)
            image = Image.open(io.BytesIO(body))
            fmt = image.format
            if fmt not in ('JPEG', 'PNG', 'GIF'):
                return 415, 'Only JPEG, PNG or GIF'
            if fmt == 'GIF':
                width, height = image.size
                if width > GIF_MAX_SIDE or height > GIF_MAX_SIDE:
                    return 413, GIF_LIMITS
                frame = 0
                while True:
                    if frame >= GIF_MAX_FRAMES or width * height * (frame + 1) > GIF_MAX_PIXELS:
                        return 413, GIF_LIMITS
                    if image.size != (width, height):
                        return 415, 'Invalid GIF frame dimensions'
                    image.load()
                    frame += 1
                    try:
                        image.seek(frame)
                    except EOFError:
                        break
            else:
                if len(body) > MAX_STATIC_SIZE:
                    return 413, 'JPEG and PNG must be up to 256 KB'
                image.verify()
                with Image.open(io.BytesIO(body)) as decoded:
                    decoded.load()
    except Exception:
        return 415, 'Invalid JPEG, PNG or GIF'
    ext = {'PNG': 'png', 'JPEG': 'jpg', 'GIF': 'gif'}[fmt]
    pic = hashlib.sha1(body).hexdigest()
    address = hmac.new(IP_SALT.encode(), ip.encode(), hashlib.sha256).hexdigest()
    now = int(time.time())
    with connect() as db:
        # One write transaction makes the rate check and insert atomic.
        db.execute('BEGIN IMMEDIATE')
        if (client and db.execute('SELECT 1 FROM installation_bans WHERE client=?',(client,)).fetchone()) or db.execute('SELECT 1 FROM banned WHERE ip=?', (address,)).fetchone():
            return 403, 'Banned'
        if db.execute('SELECT 1 FROM blocked WHERE id=?', (pic,)).fetchone():
            return 451, 'Blocked'
        count = db.execute('SELECT COUNT(*) FROM uploads WHERE ip=? AND at>?', (address, now-3600)).fetchone()[0]
        client_count = db.execute('SELECT COUNT(*) FROM client_uploads WHERE client=? AND at>?',(client,now-3600)).fetchone()[0] if client else 0
        if count >= 120 or client_count >= 120:
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
            db.execute('INSERT INTO picture_clients VALUES (?,?,?) ON CONFLICT(id,client) DO UPDATE SET at=excluded.at',(pic,client,now))
            db.execute('INSERT INTO client_uploads VALUES (?,?)',(client,now))
        else:
            db.execute('INSERT INTO legacy_uploaders VALUES (?,?,?) ON CONFLICT(id,ip) DO UPDATE SET at=excluded.at',(pic,address,now))
        if client and kind=='own':
            previous = db.execute('SELECT picture FROM picture_slots WHERE client=? AND slot=?',(client,slot)).fetchone()
            db.execute('INSERT INTO picture_slots VALUES (?,?,?,?) ON CONFLICT(client,slot) DO UPDATE SET picture=excluded.picture,at=excluded.at',
                       (client,slot,pic+'.'+ext,now))
            if previous and previous['picture'] != pic+'.'+ext:
                discard_picture(db,previous['picture'])
        else:
            db.execute('INSERT OR IGNORE INTO protected_pictures VALUES (?)',(pic,))
        db.execute('INSERT INTO uploads VALUES (?,?)', (address,now))
    return 200, ORIGIN + '/' + pic + '.' + ext


def audit(db, action, target_type, target, picture='', reason=''):
    db.execute('INSERT INTO moderation_events(at,action,target_type,target,picture,reason,actor) VALUES (?,?,?,?,?,?,?)',
               (int(time.time()),action,target_type,target,picture,reason,'admin'))
    db.execute('DELETE FROM moderation_events WHERE seq NOT IN (SELECT seq FROM moderation_events ORDER BY seq DESC LIMIT 10000)')


def ban_installation(db, client, picture, reason):
    added = db.execute('INSERT OR IGNORE INTO installation_bans VALUES (?,?,?,?)',(client,int(time.time()),picture,reason)).rowcount
    if added:
        audit(db,'ban_installation','installation',client,picture,reason)
    previous = [r['picture'] for r in db.execute('SELECT picture FROM picture_slots WHERE client=?',(client,))]
    db.execute('DELETE FROM picture_slots WHERE client=?',(client,))
    for filename in previous:
        discard_picture(db,filename)


def block_picture(db, pic, reason):
    row = db.execute('SELECT data FROM pictures WHERE id=?',(pic,)).fetchone()
    thumb = None
    if row:
        try:
            with Image.open(io.BytesIO(row['data'])) as original:
                preview = original.convert('RGB')
                preview.thumbnail((160,160))
                output = io.BytesIO()
                preview.save(output,format='JPEG',quality=60)
                thumb = output.getvalue()
        except Exception:
            pass
    if db.execute('INSERT OR IGNORE INTO blocked VALUES (?)',(pic,)).rowcount:
        db.execute('INSERT OR REPLACE INTO blocked_meta VALUES (?,?,?,?)',(pic,int(time.time()),reason,thumb))
        audit(db,'block','picture',pic,pic,reason)
    db.execute('DELETE FROM picture_slots WHERE picture LIKE ?',(pic+'.%',))
    db.execute('DELETE FROM pictures WHERE id=?',(pic,))
    # Keep at most 1,000 small, private first-frame previews, never originals.
    db.execute('UPDATE blocked_meta SET thumb=NULL WHERE thumb IS NOT NULL AND id NOT IN (SELECT id FROM blocked_meta ORDER BY at DESC,id LIMIT 1000)')


def moderate(form):
    action = form.get('action',['delete'])[0]
    target = form.get('target',[''])[0].strip().lower()
    reason = form.get('reason',[''])[0].strip()[:300] or 'Решение администратора'
    if action in ('unban_installation','unban_ip','ban_installation'):
        if not re.fullmatch(r'[a-f0-9]{64}',target):
            return 400,'Invalid installation or address ID'
    elif action in ('delete','ban','unblock'):
        target = form.get('id',[''])[0].strip().split('/')[-1].split('.')[0]
        if not re.fullmatch(r'[a-f0-9]{40}',target):
            return 400,'Invalid picture ID'
    else:
        return 400,'Invalid action'
    with connect() as db:
        db.execute('BEGIN IMMEDIATE')
        if action=='unban_installation':
            if db.execute('DELETE FROM installation_bans WHERE client=?',(target,)).rowcount:
                audit(db,action,'installation',target,reason=reason)
        elif action=='unban_ip':
            if db.execute('DELETE FROM banned WHERE ip=?',(target,)).rowcount:
                audit(db,action,'legacy_ip',target,reason=reason)
            db.execute('DELETE FROM legacy_ban_meta WHERE ip=?',(target,))
        elif action=='ban_installation':
            ban_installation(db,target,'',reason)
        elif action=='unblock':
            if db.execute('DELETE FROM blocked WHERE id=?',(target,)).rowcount:
                audit(db,action,'picture',target,target,reason)
            db.execute('DELETE FROM blocked_meta WHERE id=?',(target,))
        else:
            if not db.execute('SELECT 1 FROM pictures WHERE id=?',(target,)).fetchone():
                return (303,action) if db.execute('SELECT 1 FROM blocked WHERE id=?',(target,)).fetchone() else (404,'Picture not found')
            clients = [r['client'] for r in db.execute('SELECT client FROM picture_clients WHERE id=?',(target,))]
            addresses = [r['ip'] for r in db.execute('SELECT ip FROM legacy_uploaders WHERE id=?',(target,))]
            block_picture(db,target,reason)
            if action=='ban':
                for client in clients:
                    ban_installation(db,client,target,reason)
                for address in addresses:
                    if db.execute('INSERT OR IGNORE INTO banned VALUES (?)',(address,)).rowcount:
                        db.execute('INSERT OR REPLACE INTO legacy_ban_meta VALUES (?,?,?,?)',(address,int(time.time()),target,reason))
                        audit(db,'ban_ip','legacy_ip',address,target,reason)
            for table in ('uploaders','picture_clients','legacy_uploaders'):
                db.execute('DELETE FROM '+table+' WHERE id=?',(target,))
    return 303,action


def admin_page(query, cookie):
    cleanup()
    view = query.get('view',['pictures'])[0]
    if view not in ('pictures','bans','blocked','history'):
        return 400,'Unknown section'
    q = query.get('q',[''])[0].strip().lower()
    if q and not re.fullmatch(r'[a-f0-9]{4,64}',q):
        return 400,'Введите от 4 до 64 шестнадцатеричных символов ID.'
    try:
        page = max(0,min(100000,int(query.get('page',['0'])[0])))
    except ValueError:
        page = 0
    csrf = hmac.new(ADMIN_KEY.encode(),cookie.encode(),hashlib.sha256).hexdigest()
    escape = html.escape

    def date(at):
        return datetime.fromtimestamp(at,ZoneInfo('Europe/Moscow')).strftime('%d.%m.%Y %H:%M МСК') if at else 'Дата неизвестна (старые данные)'

    def action_form(action, target, caption, picture=False):
        field = 'id' if picture else 'target'
        return f'<form method="post" action="/admin"><input type="hidden" name="csrf" value="{csrf}"><input type="hidden" name="{field}" value="{escape(target)}"><input name="reason" maxlength="300" placeholder="Причина (необязательно)"><button name="action" value="{action}">{caption}</button></form>'

    def identifier(value):
        return '<code>'+escape(value)+'</code>'

    nav = '<nav>'+''.join(f'<a href="/admin?view={name}">{caption}</a>' for name,caption in [('pictures','Картинки'),('bans','Забаненные'),('blocked','Запрещённые картинки'),('history','История')])+'</nav>'
    cards = []
    params = []
    with connect() as db:
        if view=='pictures':
            where = "p.kind='own'"
            if q:
                where += ' AND (p.id LIKE ? OR EXISTS (SELECT 1 FROM picture_clients c WHERE c.id=p.id AND c.client LIKE ?))'
                params = [q+'%',q+'%']
            total = db.execute('SELECT COUNT(*) FROM pictures p WHERE '+where,params).fetchone()[0]
            rows = db.execute('SELECT p.* FROM pictures p WHERE '+where+' ORDER BY p.added DESC,p.id LIMIT 60 OFFSET ?',params+[page*60]).fetchall()
            for row in rows:
                filename = row['id']+'.'+row['type']
                owners = db.execute('SELECT client FROM picture_clients WHERE id=? ORDER BY client LIMIT 10',(row['id'],)).fetchall()
                ids = '<br>'.join('<a href="/admin?'+urlencode({'view':'pictures','q':owner['client']})+'">'+identifier(owner['client'])+'</a>' for owner in owners) or 'Старый клиент: ID неизвестен'
                form = action_form('delete',row['id'],'Запретить картинку',True)
                form += action_form('ban',row['id'],'Бан загрузчиков',True)
                cards.append(f'<article class="card"><a href="/{filename}" target="_blank" rel="noreferrer"><img loading="lazy" src="/{filename}" alt="Выбрано пользователем"></a><small>{date(row["added"])}<br>ID установки:<br>{ids}</small>{form}</article>')
        elif view=='bans':
            sql = "SELECT client AS target,at,picture,reason,'installation' AS kind FROM installation_bans UNION ALL SELECT b.ip,COALESCE(m.at,0),COALESCE(m.picture,''),COALESCE(m.reason,'Старый бан по IP'),'legacy_ip' FROM banned b LEFT JOIN legacy_ban_meta m ON m.ip=b.ip"
            where = ' WHERE target LIKE ?' if q else ''
            params = [q+'%'] if q else []
            total = db.execute('SELECT COUNT(*) FROM ('+sql+')'+where,params).fetchone()[0]
            rows = db.execute('SELECT * FROM ('+sql+')'+where+' ORDER BY at DESC,target LIMIT 60 OFFSET ?',params+[page*60]).fetchall()
            for row in rows:
                legacy = row['kind']=='legacy_ip'
                label = 'Старый бан по IP (хеш адреса)' if legacy else 'ID установки'
                form = action_form('unban_ip' if legacy else 'unban_installation',row['target'],'Разбанить')
                cards.append(f'<article class="card"><strong>{label}</strong><p>{identifier(row["target"])}</p><small>{date(row["at"])}</small><p>{escape(row["reason"])}</p><small>Картинка: {identifier(row["picture"])}</small>{form}</article>')
        elif view=='blocked':
            where = ' WHERE b.id LIKE ?' if q else ''
            params = [q+'%'] if q else []
            total = db.execute('SELECT COUNT(*) FROM blocked b'+where,params).fetchone()[0]
            rows = db.execute('SELECT b.id,COALESCE(m.at,0) AS at,COALESCE(m.reason,\'Старый запрет картинки\') AS reason,m.thumb FROM blocked b LEFT JOIN blocked_meta m ON m.id=b.id'+where+' ORDER BY at DESC,b.id LIMIT 60 OFFSET ?',params+[page*60]).fetchall()
            for row in rows:
                # Inline tiny private previews to avoid the nginx upload limiter
                # rejecting a gallery's simultaneous thumbnail requests.
                preview_data = base64.b64encode(row['thumb']).decode('ascii') if row['thumb'] else ''
                thumb = f'<img loading="lazy" src="data:image/jpeg;base64,{preview_data}" alt="Первый кадр запрещённой картинки">' if preview_data else '<p>Превью недоступно</p>'
                cards.append(f'<article class="card">{thumb}<small>Превью первого кадра · {date(row["at"])}</small>{identifier(row["id"])}<p>{escape(row["reason"])}</p>'+action_form('unblock',row['id'],'Разрешить картинку',True)+'</article>')
        else:
            where = ' WHERE target LIKE ? OR picture LIKE ?' if q else ''
            params = [q+'%',q+'%'] if q else []
            total = db.execute('SELECT COUNT(*) FROM moderation_events'+where,params).fetchone()[0]
            rows = db.execute('SELECT * FROM moderation_events'+where+' ORDER BY seq DESC LIMIT 60 OFFSET ?',params+[page*60]).fetchall()
            names = {'block':'Запрет картинки','unblock':'Разрешение картинки','ban_installation':'Бан установки','unban_installation':'Разбан установки','ban_ip':'Старый бан по IP','unban_ip':'Разбан по IP'}
            for row in rows:
                cards.append(f'<article class="card"><strong>{names.get(row["action"],escape(row["action"]))}</strong><small>{date(row["at"])}<br>Администратор (общий ключ)</small>{identifier(row["target"])}<p>{escape(row["reason"])}</p></article>')
    search = f'<form class="filters" method="get"><input type="hidden" name="view" value="{view}"><input name="q" value="{escape(q)}" placeholder="ID установки или картинки"><button>Найти</button><a href="/admin?view={view}">Сбросить</a></form>'
    if view=='bans' and len(q)==64:
        search += '<details><summary>Забанить установку по указанному ID</summary>'+action_form('ban_installation',q,'Забанить установку')+'</details>'
    pager = ''
    if page:
        pager += '<a href="/admin?'+urlencode({'view':view,'q':q,'page':page-1})+'">← Назад</a> '
    if (page+1)*60 < total:
        pager += '<a href="/admin?'+urlencode({'view':view,'q':q,'page':page+1})+'">Дальше →</a>'
    titles = {'pictures':'Картинки пользователей','bans':'Забаненные','blocked':'Запрещённые картинки','history':'История модерации'}
    message = '<p class="note">Действие выполнено.</p>' if query.get('done',[''])[0] in ('ban','delete','unban_installation','unban_ip','unblock','ban_installation') else ''
    explanation = 'Одна текущая картинка на слот установки. Общие файлы хранятся один раз. «Бан загрузчиков» блокирует все известные ID загрузчиков этой картинки; старые клиенты без ID блокируются по IP. Разбан установки не снимает запрет с картинки.'
    page_html = f'<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Nowcast — модерация</title>{STYLE}<h1>{titles[view]}</h1>{nav}<p class="note">{explanation}</p>{message}{search}<p>Всего: {total}</p><div class="grid">'+(''.join(cards) or '<p>Записей нет.</p>')+f'</div><nav>{pager}</nav><form method="post" action="/logout"><input type="hidden" name="csrf" value="{csrf}"><button>Выйти</button></form></html>'
    return 200,page_html


STYLE = '''<style>
:root{color-scheme:dark}body{font:16px system-ui;background:#313338;color:#eee;max-width:1150px;margin:auto;padding:24px}
a{color:#aebaff}button,input,select{font:inherit;padding:9px;border-radius:6px;border:1px solid #555;background:#232428;color:white}
button{cursor:pointer}button:hover{background:#4752c4}.ban{background:#9c3035}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:16px}
.card{background:#232428;border-radius:10px;padding:12px;text-align:center}.card img{width:100%;height:180px;object-fit:contain;background:#1e1f22;border-radius:6px}
.actions{display:flex;flex-wrap:wrap;gap:8px;justify-content:center;margin-top:12px}small{display:block;color:#aaa;margin:8px 0}nav{margin:20px 0;display:flex;gap:16px;flex-wrap:wrap}
code{overflow-wrap:anywhere}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:10px;vertical-align:top;border-bottom:1px solid #555}input[name=reason]{box-sizing:border-box;width:100%;margin-top:8px}.filters{display:flex;gap:8px;flex-wrap:wrap}.note{padding:12px;background:#232428;border-radius:8px}input[type=password]{width:min(80%,360px)}</style>'''


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
        private_thumb = re.fullmatch(r'/admin/blocked/([a-f0-9]{40})\.jpg',url.path)
        if private_thumb:
            if not self.authenticated():
                return self.send(403,'Sign in first')
            with connect() as db:
                row = db.execute('SELECT thumb FROM blocked_meta WHERE id=? AND id IN (SELECT id FROM blocked)',(private_thumb[1],)).fetchone()
            if not row or row['thumb'] is None:
                return self.send(404,'Preview unavailable')
            return self.send(200,row['thumb'],'image/jpeg')
        match = re.fullmatch(r'/([a-f0-9]{40})\.(jpg|png|gif)',url.path)
        if match:
            with connect() as db:
                now = int(time.time())
                row = db.execute("SELECT * FROM pictures WHERE id=? AND type=? AND ((kind='own' AND added>?) OR (kind='cover' AND added>?))",
                                 (match[1],match[2],now-KEEP,now-COVER_KEEP)).fetchone()
            if not row:
                return self.send(404,'Not found')
            return self.send(200,row['data'],MIME_TYPES[row['type']])
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
            # Legacy clients may still submit text. Do not read or store it.
            return self.send(410,'Text submissions are no longer accepted')
        if url.path in ('/upload','/bridge/upload'):
            if url.path.startswith('/bridge') and not self.bridge():
                return self.send(404,'Not found')
            body = self.read(MAX_SIZE)
            if body is None:
                return self.send(413,'The picture must be up to 2 MiB')
            # nginx overwrites X-Real-IP; arbitrary forwarded headers are ignored.
            ip = self.headers.get('X-Real-IP',self.client_address[0])
            source = 'vps'
            if url.path.startswith('/bridge'):
                ip = self.headers.get('X-Nowcast-IP','')
                source = 'cloudflare'
                if not ip:
                    return self.send(400,'Missing address')
            kind = 'own' if parse_qs(url.query).get('kind',[''])[0]=='own' else 'cover'
            token = (self.headers.get('X-Nowcast-Installation','') if kind=='own' else self.headers.get('X-Nowcast-Client','')) if source=='vps' else ''
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
        status, action = moderate(form)
        if status != 303:
            return self.send(status,action)
        view = {'unban_installation':'bans','unban_ip':'bans','unblock':'blocked'}.get(action,'pictures')
        return self.send(303,'',headers={'Location':'/admin?'+urlencode({'view':view,'done':action})})

    def admin(self, query):
        status, page = admin_page(query,self.cookie('nowcast_admin'))
        return self.send(status,page,'text/html; charset=utf-8')


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

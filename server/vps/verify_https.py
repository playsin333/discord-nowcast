"""End-to-end HTTPS, authentication, delete, ban and header-spoofing checks.
Only generated fixture images and reserved documentation IPs are used.
"""
import hashlib
import http.cookiejar
import io
import os
import re
import sqlite3
import urllib.error
import urllib.parse
import urllib.request
from PIL import Image

config = dict(line.strip().split('=',1) for line in open('/etc/nowcast-covers/service.env') if '=' in line)
base = config['PUBLIC_ORIGIN']
jar = http.cookiejar.CookieJar()
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
ids = []

def req(path, body=None, headers=None):
    request = urllib.request.Request(base+path,data=body,headers=headers or {})
    try:
        response = client.open(request,timeout=15)
    except urllib.error.HTTPError as e:
        response = e
    return response.status,response.read(),response.headers

def picture(color):
    buf = io.BytesIO()
    Image.new('RGB',(17,17),color).save(buf,format='PNG')
    body = buf.getvalue()
    ids.append(hashlib.sha1(body).hexdigest())
    return body

def form(**fields):
    return urllib.parse.urlencode(fields).encode()

try:
    assert req('/health')[0] == 200
    assert req('/admin')[0] == 200
    assert req('/admin',form(id='a'*40))[0] == 403
    assert req('/bridge/check?id='+'a'*40)[0] == 404
    assert req('/login',form(key=config['ADMIN_KEY']))[0] == 200
    status,page,_ = req('/admin')
    assert status == 200 and 'Картинки пользователей'.encode() in page
    csrf = re.search(rb'name="csrf" value="([a-f0-9]+)"',page).group(1).decode()
    body = picture('cyan')
    own_id = ids[-1]
    cover_body = picture('green')
    cover_id = ids[-1]
    assert req('/upload',cover_body,{'Content-Type':'image/png'})[0] == 200
    assert cover_id.encode() not in req('/admin')[1]
    status,answer,_ = req('/upload?kind=own',body,{'Content-Type':'image/png'})
    assert status == 200,(status,answer)
    path = urllib.parse.urlsplit(answer.decode()).path
    assert req(path)[1] == body
    assert req('/admin',form(id=own_id,action='delete',csrf='wrong'))[0] == 403
    assert req('/admin',form(id=own_id,action='delete',csrf=csrf))[0] == 200
    assert req(path)[0] == 404
    assert req('/upload?kind=own',body)[0] == 451
    print('HTTPS, login, CSRF, upload, download, delete and blocked re-upload: OK')
    bridge = {'Authorization':'Bearer '+config['BRIDGE_KEY'],'X-Nowcast-IP':'192.0.2.170'}
    body = picture('magenta')
    status,answer,_ = req('/bridge/upload?kind=own',body,bridge)
    assert status == 200,(status,answer)
    status,page,_ = req('/admin')
    assert ids[-1].encode() in page and 'Бан'.encode() in page
    assert req('/admin',form(id=ids[-1],action='ban',csrf=csrf))[0] == 200
    assert req('/bridge/upload?kind=own',picture('yellow'),bridge)[0] == 403
    # A direct caller cannot impersonate a Cloudflare-provided IP.
    assert req('/upload?kind=own',picture('blue'),{'X-Nowcast-IP':'192.0.2.170','X-Real-IP':'192.0.2.170','CF-Connecting-IP':'192.0.2.170'})[0] == 200
    print('Bridge authentication, gallery, ban and spoofed-IP protection: OK')
finally:
    # Remove only fixtures created by this script, never existing user data.
    db = sqlite3.connect('/var/lib/nowcast-covers/covers.db')
    for pic in ids:
        db.execute('DELETE FROM pictures WHERE id=?',(pic,))
        db.execute('DELETE FROM blocked WHERE id=?',(pic,))
        db.execute('DELETE FROM uploaders WHERE id=?',(pic,))
    import hmac
    hashed = hmac.new(config['IP_SALT'].encode(),b'192.0.2.170',hashlib.sha256).hexdigest()
    db.execute('DELETE FROM banned WHERE ip=?',(hashed,))
    db.execute('DELETE FROM uploads WHERE ip=?',(hashed,))
    db.commit()
    print('Player covers are accepted but hidden from the moderation gallery: OK')

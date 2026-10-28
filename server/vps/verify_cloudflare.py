"""Verify a deployed Worker mirrors into the VPS and obeys its moderation."""
import hashlib
import http.cookiejar
import io
import re
import secrets
import sqlite3
import urllib.error
import urllib.parse
import urllib.request
from PIL import Image

config = dict(line.strip().split('=',1) for line in open('/etc/nowcast-covers/service.env') if '=' in line)
primary = 'https://nowcast-covers.playsin333.workers.dev'
reserve = config['PUBLIC_ORIGIN']
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
client.addheaders = [('User-Agent','discord-nowcast/1.5.2 (hosting-check)')]

def request(url,body=None,headers=None):
    try:
        response = client.open(urllib.request.Request(url,data=body,headers=headers or {}),timeout=20)
    except urllib.error.HTTPError as e:
        response = e
    return response.status,response.read(),response.headers

def form(**values):
    return urllib.parse.urlencode(values).encode()

buffer = io.BytesIO()
Image.new('RGB',(19,19),tuple(secrets.token_bytes(3))).save(buffer,format='PNG')
body = buffer.getvalue()
pic = hashlib.sha1(body).hexdigest()
status,answer,_ = request(primary+'/upload?kind=own',body,{'Content-Type':'image/png'})
assert status == 200,('Cloudflare upload',status,answer[:200])
url = answer.decode().strip()
assert url == primary+'/'+pic+'.png',url
status,data,headers = request(url)
assert status == 200 and data == body,('Cloudflare download',status)
assert headers.get('Cache-Control') == 'no-store'
print('Cloudflare upload and download: OK')
with sqlite3.connect('/var/lib/nowcast-covers/covers.db') as db:
    row = db.execute('SELECT source,kind FROM pictures WHERE id=?',(pic,)).fetchone()
assert row == ('cloudflare','own'),('mirror record',row)
assert request(reserve+'/'+pic+'.png')[1] == body
status,page,_ = request(reserve+'/login',form(key=config['ADMIN_KEY']))
assert status == 200
assert pic.encode() in page, 'Missing in gallery'
csrf = re.search(rb'name="csrf" value="([a-f0-9]+)"',page).group(1).decode()
print('Mirror, source attribution and shared gallery: OK')
assert request(reserve+'/admin',form(id=pic,action='delete',csrf=csrf))[0] == 200
assert request(reserve+'/'+pic+'.png')[0] == 404
assert request(url)[0] == 451
assert request(primary+'/upload?kind=own',body)[0] == 451
assert request(reserve+'/upload?kind=own',body)[0] == 451
print('Deletion blocks both delivery and re-upload through both hosts: OK')
# Keep the moderation block: the synthetic image is still in D1 until expiry.
with sqlite3.connect('/var/lib/nowcast-covers/covers.db') as db:
    db.execute('DELETE FROM uploaders WHERE id=?',(pic,))
print('Synthetic fixture removed from gallery; no user pictures changed.')

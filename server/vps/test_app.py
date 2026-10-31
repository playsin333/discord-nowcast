"""Storage and moderation checks with real SQLite and valid images."""
import io
import os
import tempfile
import time
import unittest
from unittest.mock import patch

import app
from PIL import Image


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        app.DB = os.path.join(self.tmp.name, 'test.db')
        app.IP_SALT = 'test-salt'
        app.setup()
        buf = io.BytesIO()
        Image.new('RGB',(8,8),'red').save(buf,format='PNG')
        self.body = buf.getvalue()

    def tearDown(self):
        self.tmp.cleanup()

    def test_expiry_and_refresh(self):
        now = int(time.time())
        with patch('app.time.time',return_value=now):
            status,url = app.store(self.body,'1.2.3.4','own')
        self.assertEqual(status,200)
        with patch('app.time.time',return_value=now+app.KEEP-1):
            app.cleanup()
            self.assertEqual(app.store(self.body,'1.2.3.4','own')[0],200)
        with patch('app.time.time',return_value=now+2*app.KEEP):
            app.cleanup()
        with app.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM pictures').fetchone()[0],0)

    def test_own_classification_and_uploaders_preserved(self):
        app.store(self.body,'1.1.1.1','own','cloudflare')
        self.assertEqual(app.store(self.body,'2.2.2.2','cover','vps')[0],200)
        app.store(self.body,'2.2.2.2','own','vps')
        with app.connect() as db:
            row = db.execute('SELECT * FROM pictures').fetchone()
            self.assertEqual(row['kind'],'own')
            self.assertEqual(row['source'],'both')
            self.assertEqual(db.execute('SELECT COUNT(*) FROM uploaders').fetchone()[0],2)
            db.execute('INSERT INTO blocked VALUES (?)',(row['id'],))
            db.execute('INSERT INTO banned SELECT ip FROM uploaders WHERE id=?',(row['id'],))
        self.assertEqual(app.store(self.body,'3.3.3.3','own')[0],451)
        self.assertEqual(app.store(self.body,'1.1.1.1','own')[0],403)
        with patch('app.time.time',return_value=time.time()+app.KEEP+1):
            app.cleanup()
        self.assertEqual(app.store(self.body,'3.3.3.3','own')[0],451)

    def test_validation_and_rate_limit(self):
        self.assertEqual(app.store(self.body,'1.1.1.1','unknown')[0],400)
        self.assertEqual(app.store(b'\x89PNGfake','1.1.1.1','own')[0],415)
        self.assertEqual(app.store(b'x'*(app.MAX_SIZE+1),'1.1.1.1','own')[0],413)
        for _ in range(120):
            self.assertEqual(app.store(self.body,'1.1.1.1','own')[0],200)
        self.assertEqual(app.store(self.body,'1.1.1.1','own')[0],429)
        self.assertEqual(app.store(self.body,'2.2.2.2','own')[0],200)

    def test_gallery_only_shows_user_picked_images(self):
        status,url = app.store(self.body,'1.1.1.1','own')
        own_id = url.rsplit('/',1)[1].split('.')[0]
        old_id = 'a'*40
        with app.connect() as db:
            db.execute('INSERT INTO pictures VALUES (?,?,?,?,?,?)',
                       (old_id,'png',self.body,int(time.time()),'cover','old'))
        handler = object.__new__(app.Handler)
        handler.cookie = lambda name: 'test-session'
        handler.send = lambda status,body,mime: body
        page = handler.admin({'kind':['cover']})
        self.assertIn(own_id,page)
        self.assertNotIn(old_id,page)
        self.assertNotIn('kind=all',page)

    def test_player_cover_one_hour_and_own_six_hours(self):
        now = int(time.time())
        with patch('app.time.time',return_value=now):
            status,url = app.store(self.body,'1.2.3.4','cover')
            buf=io.BytesIO()
            Image.new('RGB',(8,8),'blue').save(buf,format='PNG')
            own_url=app.store(buf.getvalue(),'1.2.3.4','own')[1]
        self.assertEqual(status,200)
        handler=object.__new__(app.Handler)
        handler.path=url.split(app.ORIGIN)[1]
        handler.send=lambda status,body,mime=None: status
        with patch('app.time.time',return_value=now+3599):
            self.assertEqual(handler.do_GET(),200)
        with patch('app.time.time',return_value=now+3600):
            self.assertEqual(handler.do_GET(),404)
            handler.path=own_url.split(app.ORIGIN)[1]
            self.assertEqual(handler.do_GET(),200)
            app.cleanup()
        with app.connect() as db:
            self.assertEqual(db.execute("SELECT kind FROM pictures").fetchone()[0],'own')
        # A cover still in use can be renewed without touching custom pictures.
        with patch('app.time.time',return_value=now+3700):
            self.assertEqual(app.store(self.body,'1.2.3.4','cover')[0],200)
        with patch('app.time.time',return_value=now+7200):
            app.cleanup()
        with app.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM pictures').fetchone()[0],2)





class StatusTests(unittest.TestCase):
    setUp = StorageTests.setUp
    tearDown = StorageTests.tearDown
    def submit(self, ip='1.1.1.1', **fields):
        import json
        data = dict(title='Привет <script>alert(1)</script>', line2='Вторая строка', type='listening', picture='')
        data.update(fields)
        return app.store_status(json.dumps(data,ensure_ascii=False).encode(),ip)

    def test_same_picture_separate_users_and_texts(self):
        picture=app.store(self.body,'1.1.1.1','own')[1].rsplit('/',1)[1]
        app.store(self.body,'2.2.2.2','own')
        self.assertEqual(self.submit(picture=picture)[0],200)
        self.assertEqual(self.submit(ip='2.2.2.2',title='Другой текст',picture=picture)[0],200)
        self.assertEqual(self.submit(picture=picture)[0],200)
        self.assertEqual(self.submit(ip='3.3.3.3',picture=picture)[0],400)
        with app.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM statuses').fetchone()[0],2)
        handler=object.__new__(app.Handler)
        handler.cookie=lambda name:'test-session'
        handler.send=lambda status,body,mime:body
        page=handler.admin({})
        self.assertIn('Другой текст',page)
        self.assertIn('&lt;script&gt;',page)
        self.assertNotIn('<script>alert(1)</script>',page)

    def test_status_without_picture_expiry_validation(self):
        self.assertEqual(self.submit()[0],200)
        self.assertEqual(self.submit(title='')[0],400)
        self.assertEqual(self.submit(title='x'*129)[0],400)
        self.assertEqual(self.submit(picture='https://evil.invalid/image.png')[0],400)
        self.assertEqual(self.submit(type='bad')[0],400)
        self.assertEqual(app.store_status(b'[]','1.1.1.1')[0],400)
        with patch('app.time.time',return_value=time.time()+app.KEEP+1):
            app.cleanup()
        with app.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM statuses').fetchone()[0],0)

    def test_status_delete_and_ban_http_forms(self):
        import hmac,hashlib
        from urllib.parse import urlencode
        self.assertEqual(self.submit()[0],200)
        with app.connect() as db:
            status_id=db.execute('SELECT id FROM statuses').fetchone()[0]
        handler=object.__new__(app.Handler)
        handler.path='/admin'
        handler.cookie=lambda name:'test-session'
        handler.authenticated=lambda:True
        handler.send=lambda status,body,**kwargs:status
        csrf=hmac.new(app.ADMIN_KEY.encode(),b'test-session',hashlib.sha256).hexdigest()
        form=dict(status_id=status_id,action='delete',csrf='wrong')
        handler.read=lambda maximum:urlencode(form).encode()
        self.assertEqual(handler.do_POST(),403)
        form['csrf']=csrf
        self.assertEqual(handler.do_POST(),303)
        self.assertEqual(self.submit()[0],451)
        self.assertEqual(self.submit(title='Новый статус')[0],200)
        with app.connect() as db:
            form['status_id']=db.execute('SELECT id FROM statuses').fetchone()[0]
        form['action']='ban'
        self.assertEqual(handler.do_POST(),303)
        self.assertEqual(self.submit(title='Ещё статус')[0],403)
        self.assertEqual(app.store(self.body,'1.1.1.1','own')[0],403)

if __name__ == '__main__':
    unittest.main()

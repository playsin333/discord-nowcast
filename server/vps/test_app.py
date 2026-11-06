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





class PictureOnlyTests(unittest.TestCase):
    setUp = StorageTests.setUp
    tearDown = StorageTests.tearDown

    def upload(self, token, color='red', slot='status'):
        buf = io.BytesIO()
        Image.new('RGB', (8, 8), color).save(buf, format='PNG')
        status, url = app.store(buf.getvalue(), '1.1.1.1', 'own', 'vps', token, slot)
        self.assertEqual(status, 200)
        return url.rsplit('/', 1)[1]

    def test_text_endpoint_does_not_read_or_store_body(self):
        handler = object.__new__(app.Handler)
        handler.path = '/status'
        handler.read = lambda maximum: self.fail('text body must not be read')
        handler.send = lambda status, body: status
        self.assertEqual(handler.do_POST(), 410)
        with app.connect() as db:
            self.assertIsNone(db.execute("SELECT 1 FROM sqlite_master WHERE name='statuses'").fetchone())

    def test_tracked_status_picture_visible_and_replaceable(self):
        first = self.upload('a' * 64)
        handler = object.__new__(app.Handler)
        handler.cookie = lambda name: 'session'
        handler.send = lambda status, body, mime: body
        page = handler.admin({})
        self.assertIn('src="/' + first + '"', page)
        self.assertNotIn('Тексты кастомных статусов', page)
        second = self.upload('a' * 64, 'blue')
        with app.connect() as db:
            self.assertIsNone(db.execute('SELECT 1 FROM pictures WHERE id=?', (first.split('.')[0],)).fetchone())
            self.assertIsNotNone(db.execute('SELECT 1 FROM pictures WHERE id=?', (second.split('.')[0],)).fetchone())

    def test_shared_picture_survives_replacement(self):
        shared = self.upload('a' * 64)
        self.upload('b' * 64)
        self.upload('a' * 64, 'blue')
        with app.connect() as db:
            self.assertIsNotNone(db.execute('SELECT 1 FROM pictures WHERE id=?', (shared.split('.')[0],)).fetchone())
        self.assertEqual(app.store(self.body, '1.1.1.1', 'own', 'vps', 'a'*64, 'bad')[0], 400)

    def test_picture_delete_and_ban_forms(self):
        import hmac, hashlib
        from urllib.parse import urlencode
        picture = self.upload('a' * 64).split('.')[0]
        handler = object.__new__(app.Handler)
        handler.path = '/admin'
        handler.cookie = lambda name: 'session'
        handler.authenticated = lambda: True
        handler.send = lambda status, body, **kwargs: status
        csrf = hmac.new(app.ADMIN_KEY.encode(), b'session', hashlib.sha256).hexdigest()
        form = dict(id=picture, action='ban', csrf='bad')
        handler.read = lambda maximum: urlencode(form).encode()
        self.assertEqual(handler.do_POST(), 403)
        form['csrf'] = csrf
        self.assertEqual(handler.do_POST(), 303)
        self.assertEqual(app.store(self.body, '1.1.1.1', 'own')[0], 403)
        self.assertEqual(app.store(self.body, '2.2.2.2', 'own')[0], 451)

    def test_upgrade_purges_text_preserves_pictures_and_bans(self):
        _, url = app.store(self.body, '1.1.1.1', 'own')
        with app.connect() as db:
            db.execute('CREATE TABLE statuses (title TEXT)')
            db.execute("INSERT INTO statuses VALUES ('private text')")
            db.execute('CREATE TABLE blocked_statuses (id TEXT)')
            db.execute("INSERT INTO banned VALUES ('existing-ban')")
            db.execute("INSERT INTO blocked VALUES ('existing-block')")
        app.setup()
        app.setup()
        with app.connect() as db:
            self.assertIsNone(db.execute("SELECT 1 FROM sqlite_master WHERE name IN ('statuses','blocked_statuses')").fetchone())
            self.assertEqual(db.execute('SELECT COUNT(*) FROM pictures').fetchone()[0], 1)
            self.assertEqual(db.execute('SELECT ip FROM banned').fetchone()[0], 'existing-ban')
            self.assertEqual(db.execute('SELECT id FROM blocked').fetchone()[0], 'existing-block')


if __name__ == '__main__':
    unittest.main()

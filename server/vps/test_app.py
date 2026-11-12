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

    def gif(self, size=(16,16), frames=3):
        images = [Image.new('RGB',size,('red','green','blue')[i % 3]) for i in range(frames)]
        buf = io.BytesIO()
        images[0].save(buf,format='GIF',save_all=True,append_images=images[1:],
                       duration=[80 + i * 10 for i in range(frames)],loop=0,disposal=2)
        return buf.getvalue()

    def test_gif_roundtrip_and_moderation(self):
        body = self.gif()
        status,url = app.store(body,'1.2.3.4','own',token='a'*64,slot='status')
        self.assertEqual(status,200)
        self.assertTrue(url.endswith('.gif'))
        handler = object.__new__(app.Handler)
        handler.path = url.split(app.ORIGIN)[1]
        handler.send = lambda status,body,mime=None: (status,body,mime)
        self.assertEqual(handler.do_GET(),(200,body,'image/gif'))
        with Image.open(io.BytesIO(body)) as decoded:
            self.assertEqual(decoded.n_frames,3)
            self.assertEqual(decoded.info['loop'],0)
            decoded.seek(2)
            self.assertEqual(decoded.convert('RGB').getpixel((0,0)),(0,0,255))
        pic = url.rsplit('/',1)[1].split('.')[0]
        with app.connect() as db:
            db.execute('INSERT INTO blocked VALUES (?)',(pic,))
            db.execute('INSERT INTO banned SELECT ip FROM uploaders WHERE id=?',(pic,))
            db.execute('DELETE FROM pictures WHERE id=?',(pic,))
        self.assertEqual(handler.do_GET()[0],404)
        self.assertEqual(app.store(body,'2.2.2.2','own')[0],451)
        self.assertEqual(app.store(self.gif(frames=2),'1.2.3.4','own')[0],403)

    def test_gif_validation_budgets(self):
        for body in (self.gif(size=(513,16)), self.gif(frames=201), self.gif(size=(512,512),frames=153)):
            self.assertEqual(app.store(body,'1.2.3.4','own')[0],413)
        body = self.gif()
        self.assertEqual(app.store(body[:-10],'1.2.3.4','own')[0],415)
        self.assertEqual(app.store(body[:20],'1.2.3.4','own')[0],415)
        buf=io.BytesIO()
        Image.frombytes('RGB',(400,400),os.urandom(400*400*3)).save(buf,format='PNG')
        self.assertGreater(len(buf.getvalue()),app.MAX_STATIC_SIZE)
        self.assertEqual(app.store(buf.getvalue(),'1.2.3.4','own')[0],413)
        with app.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM pictures').fetchone()[0],0)

    def test_gif_replacement_and_expiry(self):
        status,url = app.store(self.gif(),'1.2.3.4','own',token='a'*64,slot='status')
        self.assertEqual(status,200)
        self.assertEqual(app.store(self.body,'1.2.3.4','own',token='a'*64,slot='status')[0],200)
        handler=object.__new__(app.Handler)
        handler.path=url.split(app.ORIGIN)[1]
        handler.send=lambda status,body,mime=None: status
        self.assertEqual(handler.do_GET(),404)
        status,url=app.store(self.gif(),'1.2.3.4','own')
        handler.path=url.split(app.ORIGIN)[1]
        with patch('app.time.time',return_value=time.time()+app.KEEP+1):
            self.assertEqual(handler.do_GET(),404)

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
        self.assertEqual(app.store(self.body, '1.1.1.1', 'own')[0], 451)
        self.assertEqual(app.store(self.body, '2.2.2.2', 'own', token='a'*64, slot='status')[0], 403)
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


class InstallationModerationTests(unittest.TestCase):
    setUp = StorageTests.setUp
    tearDown = StorageTests.tearDown

    def make_body(self, color):
        output=io.BytesIO()
        Image.new('RGB',(8,8),color).save(output,format='PNG')
        return output.getvalue()

    def upload(self, token, color='red', ip='1.2.3.4', slot='status'):
        status,url=app.store(self.make_body(color),ip,'own',token=token,slot=slot)
        self.assertEqual(status,200)
        return url.rsplit('/',1)[1].split('.')[0]

    def action(self, action, target, reason='Проверка модерации'):
        key='id' if action in ('ban','delete','unblock') else 'target'
        return app.moderate({'action':[action],key:[target],'reason':[reason]})

    def test_ban_follows_installation_and_unban_does_not_unblock_picture(self):
        first=self.upload('a'*64)
        self.upload('b'*64,'blue')
        self.assertEqual(self.action('ban',first)[0],303)
        self.assertEqual(app.store(self.make_body('green'),'8.8.8.8','own',token='a'*64,slot='status')[0],403)
        self.assertEqual(app.store(self.make_body('green'),'1.2.3.4','own',token='b'*64,slot='status')[0],200)
        # Installation bans also cover automatic uploads carrying the ID.
        self.assertEqual(app.store(self.make_body('green'),'9.9.9.9','cover',token='a'*64)[0],403)
        self.assertEqual(self.action('unban_installation',app.installation('a'*64))[0],303)
        self.assertEqual(app.store(self.make_body('red'),'8.8.8.8','own',token='a'*64,slot='status')[0],451)
        self.assertEqual(app.store(self.make_body('green'),'8.8.8.8','own',token='a'*64,slot='status')[0],200)
        self.assertEqual(self.action('unblock',first)[0],303)
        self.assertEqual(app.store(self.make_body('red'),'8.8.8.8','own',token='a'*64,slot='status')[0],200)
        with app.connect() as db:
            actions=[r['action'] for r in db.execute('SELECT action FROM moderation_events ORDER BY seq')]
            self.assertEqual(actions,['block','ban_installation','unban_installation','unblock'])
            self.assertEqual(db.execute('SELECT COUNT(*) FROM banned').fetchone()[0],0)

    def test_one_current_file_per_slot_and_shared_files_are_not_duplicated(self):
        red=self.upload('a'*64)
        self.upload('b'*64)
        self.upload('a'*64)
        with app.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM pictures').fetchone()[0],1)
        self.upload('a'*64,'blue')
        with app.connect() as db:
            self.assertIsNotNone(db.execute('SELECT 1 FROM pictures WHERE id=?',(red,)).fetchone())
        self.upload('b'*64,'blue')
        self.upload('a'*64,'green',slot='fallback')
        self.upload('a'*64,'yellow',slot='status')
        with app.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM picture_slots WHERE client=?',(app.installation('a'*64),)).fetchone()[0],2)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM pictures').fetchone()[0],3)
            self.assertIsNone(db.execute('SELECT 1 FROM pictures WHERE id=?',(red,)).fetchone())

    def test_private_preview_and_persistent_ban_after_expiry(self):
        pic=self.upload('a'*64)
        self.action('ban',pic,'<script>alert(1)</script>')
        handler=object.__new__(app.Handler)
        handler.path='/admin/blocked/'+pic+'.jpg'
        handler.send=lambda status,body,mime=None: (status,body,mime)
        handler.authenticated=lambda:False
        self.assertEqual(handler.do_GET()[0],403)
        handler.authenticated=lambda:True
        status,preview,mime=handler.do_GET()
        self.assertEqual((status,mime),(200,'image/jpeg'))
        self.assertLess(len(preview),20000)
        handler.path='/'+pic+'.png'
        self.assertEqual(handler.do_GET()[0],404)
        with patch('app.time.time',return_value=time.time()+app.KEEP+10):
            app.cleanup()
            self.assertEqual(app.store(self.make_body('green'),'8.8.8.8','own',token='a'*64,slot='status')[0],403)
        app.setup();app.setup()
        status,page=app.admin_page({'view':['bans'],'q':[app.installation('a'*64)]},'session')
        self.assertEqual(status,200)
        self.assertIn('Разбанить',page)
        self.assertIn('&lt;script&gt;',page)
        self.assertNotIn('<script>',page)
        self.assertNotIn('a'*64,page) # Private token is never shown.
        self.action('unban_installation',app.installation('a'*64))
        self.assertEqual(app.admin_page({'view':['blocked']},'session')[0],200)
        self.assertEqual(app.admin_page({'view':['history'],'q':[app.installation('a'*64)[:12]]},'session')[0],200)
        self.assertEqual(app.admin_page({'q':["' OR 1=1"]},'session')[0],400)

    def test_legacy_bans_can_be_removed_without_weakening_picture_ban(self):
        status,url=app.store(self.make_body('red'),'1.2.3.4','own')
        self.assertEqual(status,200)
        pic=url.rsplit('/',1)[1].split('.')[0]
        self.action('ban',pic)
        with app.connect() as db:
            address=db.execute('SELECT ip FROM banned').fetchone()[0]
        self.assertEqual(app.store(self.make_body('blue'),'1.2.3.4','own')[0],403)
        self.action('unban_ip',address)
        self.assertEqual(app.store(self.make_body('blue'),'1.2.3.4','own')[0],200)
        self.assertEqual(app.store(self.make_body('red'),'1.2.3.4','own')[0],451)

    def test_migration_preserves_old_data_and_recovers_current_installation_links(self):
        pic=self.upload('a'*64)
        app.store(self.make_body('blue'),'5.6.7.8','own')
        with app.connect() as db:
            db.execute("INSERT INTO banned VALUES (?)",('c'*64,))
            db.execute("INSERT INTO blocked VALUES (?)",('d'*40,))
            for table in ('picture_clients','legacy_uploaders','installation_bans','legacy_ban_meta','blocked_meta','moderation_events','client_uploads'):
                db.execute('DROP TABLE '+table)
        app.setup();app.setup()
        with app.connect() as db:
            self.assertEqual(db.execute('SELECT client FROM picture_clients WHERE id=?',(pic,)).fetchone()[0],app.installation('a'*64))
            self.assertEqual(db.execute('SELECT COUNT(*) FROM legacy_uploaders WHERE id=?',(pic,)).fetchone()[0],0)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM pictures').fetchone()[0],2)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM banned').fetchone()[0],1)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM blocked').fetchone()[0],1)
        self.assertEqual(self.action('ban',pic)[0],303)
        self.assertEqual(app.store(self.make_body('green'),'8.8.8.8','own',token='a'*64,slot='status')[0],403)

    def test_admin_mutations_require_authentication_csrf_and_valid_targets(self):
        import hmac,hashlib
        from urllib.parse import urlencode
        pic=self.upload('a'*64)
        handler=object.__new__(app.Handler)
        handler.path='/admin';handler.cookie=lambda name:'session'
        handler.send=lambda status,body,**kwargs:status
        handler.authenticated=lambda:False
        form={'action':'ban','id':pic,'csrf':'bad'}
        handler.read=lambda maximum:urlencode(form).encode()
        self.assertEqual(handler.do_POST(),403)
        handler.authenticated=lambda:True
        self.assertEqual(handler.do_POST(),403)
        form['csrf']=hmac.new(app.ADMIN_KEY.encode(),b'session',hashlib.sha256).hexdigest()
        self.assertEqual(handler.do_POST(),303)
        form.update(action='unban_installation',target=app.installation('a'*64),csrf='bad')
        self.assertEqual(handler.do_POST(),403)
        form['csrf']=hmac.new(app.ADMIN_KEY.encode(),b'session',hashlib.sha256).hexdigest()
        self.assertEqual(handler.do_POST(),303)
        self.assertEqual(self.action('unblock','wrong')[0],400)
        self.assertEqual(self.action('unban_installation','wrong')[0],400)
        self.assertEqual(self.action('unban_installation',app.installation('a'*64))[0],303)


if __name__ == '__main__':
    unittest.main()

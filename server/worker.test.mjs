import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { test } from 'node:test';

const source=readFileSync(new URL('./worker.js',import.meta.url),'utf8');
const worker=(await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'))).default;
const primary='https://worker.example',vps='https://nowcast.85-192-42-111.sslip.io:10443';
let dbReads=0,mirrorStatus=200,blocked=false,bridgeDown=false;
const env={BRIDGE_KEY:'test-bridge',DB:{prepare(){dbReads++;throw Error("D1 daily row read limit");}}};
const files=new Map();
globalThis.fetch=async(url,options={})=>{
  assert.ok(url.startsWith(vps+'/'));
  if(bridgeDown)throw Error('offline');
  const path=new URL(url).pathname;
  if(path.startsWith('/bridge/'))assert.equal(options.headers.Authorization,'Bearer test-bridge');
  if(path==='/bridge/check')return Response.json({blocked});
  if(path==='/bridge/upload'){
    assert.equal(options.headers['X-Nowcast-IP'],'192.0.2.100');
    if(mirrorStatus!==200)return new Response('rejected',{status:mirrorStatus});
    const data=new Uint8Array(options.body);
    if(data[0]!==0x89)return new Response('Invalid image',{status:415});
    const id=createHash('sha1').update(data).digest('hex');
    files.set('/'+id+'.png',data);
    return new Response(vps+'/'+id+'.png');
  }
  if(blocked||!files.has(path))return new Response('Not found',{status:404});
  return new Response(options.method==='HEAD'?null:files.get(path),{headers:{'Content-Type':'image/png'}});
};
const bytes=new Uint8Array([0x89,0x50,0x4e,0x47,1,2,3]);
const upload=body=>worker.fetch(new Request(primary+'/upload?kind=own',{method:'POST',body:body??bytes,headers:{'CF-Connecting-IP':'192.0.2.100'}}),env);

test('uploads and delivery work with exhausted D1, moderation remains shared',async()=>{
  const uploaded=await upload();assert.equal(uploaded.status,200);
  const url=await uploaded.text();assert.match(url,/^https:\/\/worker\.example\/[a-f0-9]{40}\.png$/);
  const shown=await worker.fetch(new Request(url),env);
  assert.equal(shown.status,200);assert.deepEqual(new Uint8Array(await shown.arrayBuffer()),bytes);
  assert.equal(shown.headers.get('Cache-Control'),'no-store');
  assert.equal(dbReads,0,'new uploads and images must not touch D1');
  const player=await worker.fetch(new Request(primary+'/upload',{method:'POST',body:bytes,headers:{'CF-Connecting-IP':'192.0.2.100'}}),env);
  assert.equal(player.status,200);
  assert.equal(dbReads,0,'player uploads must also work without D1');
  blocked=true;assert.equal((await worker.fetch(new Request(url),env)).status,451);blocked=false;
  for(const status of [403,451,429]){mirrorStatus=status;assert.equal((await upload()).status,status);}
  mirrorStatus=200;
  assert.equal((await upload(new Uint8Array(256*1024+1))).status,413);
  assert.equal((await upload(new Uint8Array([1,2]))).status,415);
  bridgeDown=true;assert.equal((await upload()).status,503);
  assert.equal((await worker.fetch(new Request(url),env)).status,503);bridgeDown=false;
  const admin=await worker.fetch(new Request(primary+'/admin?key=private'),env);
  assert.equal(admin.status,302);assert.equal(admin.headers.get('Location'),vps+'/admin');
  const secret=env.BRIDGE_KEY;env.BRIDGE_KEY='';assert.equal((await upload()).status,503);env.BRIDGE_KEY=secret;
  files.clear();assert.equal((await worker.fetch(new Request(url),env)).status,503,'legacy D1 outage is handled');
  assert.equal(dbReads,1);
  await worker.scheduled({},env);
  assert.equal((await worker.fetch(new Request(primary+'/'),env)).status,200);
});

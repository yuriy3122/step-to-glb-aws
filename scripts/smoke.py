#!/usr/bin/env python3
"""Real HTTP upload/conversion/download smoke test; API_URL and ID_TOKEN select AWS."""
import argparse, json, os, pathlib, time, urllib.request, urllib.error, struct
p=argparse.ArgumentParser()
p.add_argument('--api',default=os.getenv('API_URL','http://127.0.0.1:5080'))
p.add_argument('--input',type=pathlib.Path,default=pathlib.Path('build/native/fixtures/assembly.step'))
p.add_argument('--output',type=pathlib.Path,default=pathlib.Path('artifacts/smoke.glb'))
p.add_argument('--timeout',type=int,default=1800)
a=p.parse_args()
def request(path,body=None,token=None):
    headers={}
    if token or os.getenv('ID_TOKEN'): headers['Authorization']='Bearer '+(token or os.environ['ID_TOKEN'])
    data=None
    if body is not None: data=json.dumps(body).encode();headers['Content-Type']='application/json'
    with urllib.request.urlopen(urllib.request.Request(a.api+path,data=data,headers=headers),timeout=60) as r:return json.load(r)
source=a.input.read_bytes()
session=request('/api/jobs',{'fileName':a.input.name,'bytes':len(source)})
job=session['job']; jid=job['jobId']
with urllib.request.urlopen(urllib.request.Request(session['uploadUrl'],data=source,method='PUT',headers={'Content-Type':'application/octet-stream'}),timeout=300) as r: assert r.status in (200,204)
request('/api/jobs/'+jid+'/upload/complete',{})
deadline=time.monotonic()+a.timeout
previous=None
while time.monotonic()<deadline:
    progress=request('/api/jobs/'+jid+'/progress')
    signature=(progress['status'],progress['stage'],progress['attemptId'])
    if signature!=previous: print(json.dumps({'jobId':jid,'status':progress['status'],'stage':progress['stage'],'attempt':progress['attemptNumber']}),flush=True);previous=signature
    assert progress['status']=='SUCCEEDED' or progress['progressPercent'] is None or progress['progressPercent']<100
    if progress['status']=='SUCCEEDED':break
    if progress['status'] in ('FAILED','CANCELLED'):raise RuntimeError(json.dumps(progress))
    time.sleep(2)
else:raise TimeoutError('Conversion did not finish before deadline')
url=request('/api/jobs/'+jid+'/result')['url']
with urllib.request.urlopen(url,timeout=300) as r:glb=r.read()
magic,version,length=struct.unpack('<III',glb[:12])
assert magic==0x46546c67 and version==2 and length==len(glb) and len(glb)>28
a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_bytes(glb)
assert any(j['jobId']==jid for j in request('/api/jobs')['jobs'])
other=os.getenv('OTHER_TENANT_ID_TOKEN')
if other:
    for path,body in [(f'/api/jobs/{jid}',None),(f'/api/jobs/{jid}/progress',None),(f'/api/jobs/{jid}/result',None),(f'/api/jobs/{jid}/cancel',{})]:
        try:
            request(path,body,other)
            raise AssertionError('Another tenant accessed the job')
        except urllib.error.HTTPError as error:
            assert error.code==404, f'Expected tenant isolation 404; received {error.code}'
    print('PASS: second tenant cannot read status/progress/result or cancel the first tenant job')
print(f'PASS: real upload -> worker -> confirmed success -> authorized GLB ({len(glb)} bytes), jobId={jid}')

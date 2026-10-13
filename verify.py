import json, os, subprocess, tempfile, time, urllib.request, urllib.error
from pathlib import Path
root=Path(__file__).parent
with tempfile.TemporaryDirectory() as d:
    env=os.environ|{'ASPNETCORE_URLS':'http://127.0.0.1:16297','DISPATCH_DATA':d+'/jobs.json'}
    def start():
        p=subprocess.Popen(['/home/ubuntu/.local/share/dotnet/dotnet',str(root/'api/bin/Debug/net8.0/DispatchDesk.Api.dll')],cwd=root/'api',env=env,stdout=subprocess.DEVNULL)
        for _ in range(100):
            try: req('GET');return p
            except (OSError,urllib.error.URLError):time.sleep(.1)
        p.terminate();raise RuntimeError('Startup failed')
    def req(method,path='/api/jobs',data=None):
        r=urllib.request.Request('http://127.0.0.1:16297'+path,data=json.dumps(data).encode() if data is not None else None,method=method,headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(r) as response:return json.loads(response.read() or b'null')
    p=start()
    try:
        for data in ({'name':'','kind':'report'},{'name':'x','kind':'unknown'}):
            try:req('POST',data=data);raise AssertionError('Invalid accepted')
            except urllib.error.HTTPError as e:assert e.code==400
        ids={k:req('POST',data={'name':'test '+k,'kind':k})['id'] for k in ['report','unstable','broken']}
        for _ in range(150):
            jobs={j['kind']:j for j in req('GET')}
            if jobs['broken']['status']=='dead-letter' and jobs['unstable']['status']=='completed':break
            time.sleep(.2)
        assert jobs['report']['status']=='completed' and jobs['report']['attempts']==1
        assert jobs['unstable']['status']=='completed' and jobs['unstable']['attempts']==2
        assert jobs['broken']['status']=='dead-letter' and jobs['broken']['attempts']==3
        before=req('GET');p.terminate();p.wait(timeout=10);p=start();assert req('GET')==before
        req('POST','/api/jobs/'+ids['broken']+'/retry')
        assert next(j for j in req('GET') if j['id']==ids['broken'])['status'] in ['queued','processing']
        try:req('POST','/api/jobs/'+ids['report']+'/retry');raise AssertionError('Completed retry accepted')
        except urllib.error.HTTPError as e:assert e.code==409
        for i in range(20):req('POST',data={'name':f'capacity {i}','kind':'report'}) if i<19 else None
        try:req('POST',data={'name':'over capacity','kind':'report'});raise AssertionError('Capacity exceeded')
        except urllib.error.HTTPError as e:assert e.code==429
        print('PASS: invalid input, success, retry recovery, dead letter, persistence restart, manual requeue, retry conflict, active capacity')
    finally:p.terminate();p.wait(timeout=10)

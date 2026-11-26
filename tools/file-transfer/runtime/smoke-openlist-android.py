"""Test the trimmed Android binary in an isolated adb-shell instance.

This verifies the binary/API profile, not the MPT app sandbox or live cloud credentials.
"""
import argparse
import subprocess,json,secrets,urllib.request,base64,time,uuid
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--device', required=True)
parser.add_argument('--binary', required=True)
args = parser.parse_args()
serial=args.device;root='/data/local/tmp/mpt-openlist-qa-'+uuid.uuid4().hex;port=25244
adb=['adb','-s',serial]
def shell(*args):return subprocess.run(adb+['shell',*args],check=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE).stdout
shell('mkdir','-p',root+'/files')
subprocess.run(adb+['push',args.binary,root+'/openlist'],check=True,stdout=subprocess.DEVNULL)
shell('chmod','700',root+'/openlist')
password=secrets.token_urlsafe(24)
shell(root+'/openlist','admin','set',password,'--data',root+'/data')
subprocess.run(adb+['forward',f'tcp:{port}',f'tcp:{port}'],check=True,stdout=subprocess.DEVNULL)
cmd=f'echo $$; exec env OPENLIST_ADDR=127.0.0.1 OPENLIST_HTTP_PORT={port} {root}/openlist server --data {root}/data'
process=subprocess.Popen(adb+['shell',cmd],stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
pid=int(process.stdout.readline().strip())
def request(path,body=None,headers=None,method=None):
 data=json.dumps(body).encode() if isinstance(body,dict) else body
 h={'Content-Type':'application/json'};h.update(headers or {})
 with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:'+str(port)+path,data=data,headers=h,method=method),timeout=10) as r:return r.read()
try:
 for _ in range(50):
  try:
   result=json.loads(request('/api/auth/login',{'username':'admin','password':password}));token=result['data']['token'];break
  except Exception:time.sleep(.2)
 else:raise RuntimeError('Isolated runtime did not start/login')
 auth={'Authorization':token}
 drivers=json.loads(request('/api/admin/driver/names',headers=auth))['data']
 assert set(drivers)=={'Quark','UC','BaiduNetdisk','Local','WebDav','OpenList'},drivers
 for name,field in [('Quark','cookie'),('BaiduNetdisk','refresh_token')]:
  info=request('/api/admin/driver/info?driver='+name,headers=auth).decode();assert field in info
 create=json.loads(request('/api/admin/storage/create',{'mount_path':'/qa','driver':'Local','addition':json.dumps({'root_folder_path':root+'/files'})},auth))
 assert create['code']==200,create
 payload=b'MPT trimmed OpenList upload/download acceptance\n'
 result=json.loads(request('/api/fs/put',payload,dict(auth,**{'File-Path':'/qa/accept.txt','Content-Type':'application/octet-stream'}),'PUT'))
 assert result['code']==200,result
 basic='Basic '+base64.b64encode(('admin:'+password).encode()).decode()
 assert request('/dav/qa/accept.txt',headers={'Authorization':basic})==payload
 assert b'<html' in request('/@manage').lower()
 print(json.dumps({'drivers':drivers,'adminLogin':True,'providerCredentialFields':True,'uploadAndDavDownload':True,'adminFrontend':True}))
finally:
 shell('kill',str(pid));process.wait(timeout=10)
 subprocess.run(adb+['forward','--remove',f'tcp:{port}'],check=True,stdout=subprocess.DEVNULL)
 shell('rm','-r',root)

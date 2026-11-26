import importlib.util,urllib.request,os,subprocess
spec=importlib.util.spec_from_file_location('smoke','/opt/mpt-relay/deploy/smoke.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
o=urllib.request.build_opener(urllib.request.ProxyHandler({}));o.addheaders=[('Host','mpt-relay.tail.lixinrui000.cn')];urllib.request.install_opener(o)
original=m.Client.call
def call(self,*args,**kwargs):
 kwargs['extra_headers']={**kwargs.get('extra_headers',{}),'Host':'mpt-relay.tail.lixinrui000.cn'}
 return original(self,*args,**kwargs)
m.Client.call=call
c=m.Client('http://127.0.0.1:18766','smoke-connector-'+os.urandom(4).hex(),os.urandom(32).hex())
m.check_health(c);m.check_registration(c);mid=m.check_dav_round_trip(c,os.urandom(1024*1024));m.check_namespace_isolation(c);m.check_changes(c,1)
print('PASS health/register/1MiB DAV roundtrip/namespace isolation/longpoll')
subprocess.run(['systemctl','restart','mpt-tail-connector.service'],check=True)
subprocess.run(['systemctl','restart','mpt-tail-connect.socket'],check=True)
import time
for i in range(15):
 try:m.check_health(c);break
 except Exception:time.sleep(1)
else:raise RuntimeError('Restart recovery failed')
s,_,body=c.dav('GET',f'assistant/{c.conversation_id}/{mid}/manifest.json');assert s==200
m.check_dav_round_trip(c,os.urandom(65536))
print('PASS connector restart/persisted authenticated read/new write')

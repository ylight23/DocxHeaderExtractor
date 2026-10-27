import json,pathlib,hashlib,datetime
root=pathlib.Path(__file__).parent
load=lambda p:json.loads(p.read_text(encoding='utf-8'))
wire=load(root/'wire-integrity-audit.json'); bridge=load(root/'bridge-calls.json')['calls']; total=len(bridge)
# Add concurrency peak to the transport log, computed from the captured HTTP request intervals.
events=[]
for c in bridge:
 events.append((datetime.datetime.fromisoformat(c['StartedUtc']).timestamp(),1));events.append((datetime.datetime.fromisoformat(c['CompletedUtc']).timestamp(),-1))
active=peak=0
for _,delta in sorted(events,key=lambda x:(x[0],x[1])): active+=delta;peak=max(peak,active)
bridge_data=load(root/'bridge-calls.json');bridge_data['maxConcurrentCalls']=peak
(root/'bridge-calls.json').write_text(json.dumps(bridge_data,indent=2),encoding='utf-8')
# Full alias to SELECT-unit/page map from the actual request packets.
(root/'alias-unit-page-map.json').write_text(json.dumps(wire['scope']['aliasPageMapping'],indent=2,ensure_ascii=False),encoding='utf-8')
pairs=[]; selected=[]; phases={}
for p in sorted((root/'luna-requests').glob('*.request.json')):
 key=p.name.removesuffix('.request.json'); req=load(p); response=root/'luna-responses'/(key+'.response.json'); r=load(response)
 packet_text=next(x['text'] for x in req['messages'][1]['content'] if x['type']=='text')
 try: packet=json.loads(packet_text); stage=packet.get('stage') or 'SELECT'
 except: stage='SELECT'
 phase=next(x['Phase'] for x in bridge if x['RequestSha256']==key); phases[phase]=phases.get(phase,0)+1
 pairs.append({'phase':phase,'requestSha256':key,'responseFile':response.name,'responseSha256':hashlib.sha256(response.read_bytes()).hexdigest()})
 if phase=='SELECT':
  semantic=json.loads(r['content'])
  selected.extend({'alias':d['alias'],'decision':d['decision'],'sourceText':d.get('sourceText'),'unitId':packet.get('unitId'), 'requestSha256':key} for d in semantic['decisions'])
semantic={'status':'FROZEN_BEFORE_GOLD','sourceSha256':load(root/'run-identity.json')['sourceSha256'],'selectionInstruction':'PdfMembershipInstructionV12.Instruction (unchanged)',
 'selectedAliasCount':sum(1 for d in selected if d['decision']=='SELECT'),'decisionCount':len(selected),'decisions':selected,'requestReplyPairs':pairs,
 'freezeNote':'All 650 target decisions and the production GROUP/PLACE outputs are captured in the bound request/response files. No Gold file or score was opened before this freeze.'}
frozen_path=root/'frozen-decisions.json'; frozen_path.write_text(json.dumps(semantic,indent=2,ensure_ascii=False),encoding='utf-8')
manifest={'frozenDecisionFile':'frozen-decisions.json','frozenDecisionsSha256':hashlib.sha256(frozen_path.read_bytes()).hexdigest(),'status':'FROZEN_BEFORE_GOLD',
 'phases':phases,'transportRetriesUsed':0,'requestCount':len(pairs),'requestReplyPairsCount':len(pairs)}
(root/'run-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
# Write a repeatable pre-Gold artifact hash manifest, excluding itself and earlier attempt-01 diagnostics.
files=[]
for p in root.rglob('*'):
 if p.is_file() and p.name!='pre-gold-freeze.json':
  data=p.read_bytes();files.append({'path':p.relative_to(root).as_posix(),'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()})
freeze={'status':'FROZEN_BEFORE_GOLD','sourceSha256':semantic['sourceSha256'],'fileCount':len(files),'totalBytes':sum(f['bytes'] for f in files),'manifestSelfExcluded':True,'files':sorted(files,key=lambda x:x['path'])}
(root/'pre-gold-freeze.json').write_text(json.dumps(freeze,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({'freezeStatus':freeze['status'],'files':len(files),'bytes':freeze['totalBytes'],'selected':semantic['selectedAliasCount'],'decisions':semantic['decisionCount'],'packets':len(pairs),'phases':phases,'concurrencyPeak':peak,'frozenSha256':hashlib.sha256(frozen_path.read_bytes()).hexdigest()}))

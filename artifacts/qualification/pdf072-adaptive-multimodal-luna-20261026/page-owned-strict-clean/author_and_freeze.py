import hashlib,json,pathlib,time
p=pathlib.Path('artifacts/qualification/pdf072-adaptive-multimodal-luna-20261026/page-owned-strict-clean')
m=json.loads((p/'pre-call-packet-manifest.json').read_text(encoding='utf-8'))
selected={
1:['L0000:S0','L0001:S0','L0004:S0','L0025:S0','L0026:S0'],
2:[],
3:['L0078:S0','L0081:S0','L0089:S0','L0107:S0'],
4:['L0123:S0','L0137:S0','L0150:S0'],
5:['L0165:S0','L0166:S0'],
6:['L0219:S0'],
7:['L0271:S0','L0272:S0','L0288:S0'],
8:['L0315:S0','L0334:S0','L0335:S0'],
9:['L0359:S0','L0360:S0','L0377:S0'],
10:['L0400:S0','L0420:S0'],
11:[],
12:['L0470:S0','L0507:S0'],
13:['L0513:S0','L0514:S0','L0519:S0','L0520:S0','L0521:S0','L0523:S0','L0526:S0','L0531:S0'],
14:['L0536:S0','L0539:S0','L0540:S0','L0553:S0'],
15:['L0556:S0','L0557:S0','L0566:S0','L0576:S0','L0587:S0']}
all_selected=set(sum(selected.values(),[]))
manifest=[]
for packet in m['packets']:
 request_bytes=(p/packet['requestFile']).read_bytes(); request_sha=hashlib.sha256(request_bytes).hexdigest()
 assert request_sha==packet['requestSha256']
 req=json.loads(request_bytes); payload=json.loads(req['messages'][1]['content'][0]['text'])
 rows=payload['targets']; targets={x['alias']:x for x in rows}
 page=packet['page']; own=[a for a in selected[page] if a in targets]
 assert set(own)==set(selected[page]), (page, selected[page], targets.keys())
 decisions=[]
 for alias in targets:
  if alias in all_selected:
   decisions.append({'alias':alias,'decision':'SELECT','evidenceRefs':[alias],'sourceText':targets[alias]['text']})
  else: decisions.append({'alias':alias,'decision':'NOT_SELECT','evidenceRefs':[]})
 body={'protocolVersion':'PDF_SELECT_SOURCE_BOUND_V1','status':'COMPLETE','decisions':decisions}
 bodybytes=json.dumps(body,ensure_ascii=False,separators=(',',':')).encode('utf-8')
 body_sha=hashlib.sha256(bodybytes).hexdigest()
 bodyfile=p/'responses'/f'{request_sha}.response-body.json'; bodyfile.write_bytes(bodybytes)
 bound={'requestSha256':request_sha,'model':'local-luna-independent-evaluator','provider':'local-authored-no-provider-call','content':bodybytes.decode('utf-8'),'responseBodySha256':body_sha,'responseBytes':len(bodybytes),'status':'COMPLETE'}
 boundbytes=json.dumps(bound,ensure_ascii=False,separators=(',',':')).encode('utf-8')
 boundfile=p/'responses'/f'{request_sha}.bound.json'; boundfile.write_bytes(boundbytes)
 manifest.append({'page':page,'part':packet['part'],'requestSha256':request_sha,'requestBytes':len(request_bytes),'imageBytes':packet['imageBytes'],'responseBodySha256':body_sha,'responseBytes':len(bodybytes),'boundSha256':hashlib.sha256(boundbytes).hexdigest(),'aliases':packet['aliases'],'selectAliases':own,'requestFile':packet['requestFile'],'responseFile':str(bodyfile.relative_to(p)),'boundFile':str(boundfile.relative_to(p))})
assert sum(len(x['aliases']) for x in manifest)==650
assert len({a for x in manifest for a in x['aliases']})==650
assert set(a for x in manifest for a in x['selectAliases'])==all_selected
freeze={'format':'page-owned-strict-clean-freeze-v1','frozenUtc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'documentSha256':m['documentSha256'],'instructionSha256':m['instructionSha256'],'sourceAtomsSha256':hashlib.sha256((p/'fresh-source-atoms.json').read_bytes()).hexdigest(),'pageRenders':[{'page':i,'pngSha256':hashlib.sha256((p/'rendered-pages'/f'page-{i:02d}.png').read_bytes()).hexdigest(),'overviewJpegSha256':hashlib.sha256((p/'rendered-pages'/f'page-{i:02d}.jpg').read_bytes()).hexdigest()} for i in range(1,16)],'targetCount':650,'selectedCount':len(all_selected),'packetCount':len(manifest),'maxConcurrencyPlanned':6,'providerCalls':0,'frozenPackets':manifest}
fbytes=json.dumps(freeze,ensure_ascii=False,indent=2).encode('utf-8'); (p/'frozen-run-manifest.json').write_bytes(fbytes)
print(f'FROZEN targetCount=650 selected={len(all_selected)} packets={len(manifest)} requestBytes={min(x["requestBytes"] for x in manifest)}..{max(x["requestBytes"] for x in manifest)} responseBytes={min(x["responseBytes"] for x in manifest)}..{max(x["responseBytes"] for x in manifest)} totalImageBytes={sum(x["imageBytes"] for x in manifest)} manifestSha256={hashlib.sha256(fbytes).hexdigest()}')
for x in manifest:print(f'page={x["page"]} selected={len(x["selectAliases"])} req={x["requestBytes"]} resp={x["responseBytes"]} image={x["imageBytes"]}')

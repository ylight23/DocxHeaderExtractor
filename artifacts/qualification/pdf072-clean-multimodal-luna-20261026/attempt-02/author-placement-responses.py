import json, pathlib
root=pathlib.Path(__file__).parent
parents={'L0000:S0':'NONE','L0001:S0':'NONE','L0004:S0':'ROOT','L0025:S0':'ROOT','L0026:S0':'L0025:S0','L0078:S0':'L0025:S0','L0081:S0':'L0078:S0','L0089:S0':'L0078:S0','L0107:S0':'L0078:S0','L0123:S0':'L0078:S0','L0137:S0':'L0078:S0','L0150:S0':'L0078:S0','L0165:S0':'ROOT','L0166:S0':'L0165:S0','L0219:S0':'L0165:S0','L0271:S0':'ROOT','L0272:S0':'L0271:S0','L0288:S0':'L0271:S0','L0315:S0':'L0271:S0','L0334:S0':'ROOT','L0335:S0':'L0334:S0','L0359:S0':'L0334:S0','L0377:S0':'L0334:S0','L0400:S0':'L0334:S0','L0420:S0':'L0334:S0','L0470:S0':'L0334:S0','L0507:S0':'ROOT','L0519:S0':'ROOT','L0520:S0':'L0519:S0','L0521:S0':'L0520:S0','L0523:S0':'L0520:S0','L0526:S0':'L0520:S0','L0531:S0':'L0520:S0','L0536:S0':'L0520:S0','L0539:S0':'L0519:S0','L0540:S0':'L0539:S0','L0553:S0':'L0539:S0','L0556:S0':'ROOT','L0557:S0':'L0556:S0','L0566:S0':'L0556:S0','L0576:S0':'L0556:S0','L0587:S0':'L0556:S0'}
for p in (root/'luna-requests').glob('*.request.json'):
 req=json.loads(p.read_text(encoding='utf-8')); sha=p.name.split('.')[0]; out=root/'luna-responses'/f'{sha}.response.json'
 if out.exists(): continue
 try: packet=json.loads(next(x['text'] for x in req['messages'][1]['content'] if x['type']=='text'))
 except Exception: continue
 if packet.get('stage')!='HIERARCHY_PLACE': continue
 placements=[{'alias':h['alias'],'parent':parents[h['alias']]} for h in packet.get('headings',[])]
 body={'protocolVersion':'HEADING_PARENT_PROPOSAL_V2_CSHARP_GRAPH_VALIDATION','placements':placements}
 out.write_text(json.dumps({'requestSha256':sha,'content':json.dumps(body,ensure_ascii=False,separators=(',',':')),'finishReason':'stop'},ensure_ascii=False,indent=2),encoding='utf-8')

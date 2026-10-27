import base64,hashlib,json,pathlib,re
root=pathlib.Path(__file__).parent
load=lambda p:json.loads(p.read_text(encoding='utf-8'))
manifest=load(root/'image-manifest.json'); image={x['page']:x for x in manifest['pages']}
bridge=load(root/'bridge-calls.json')['calls']; bridge_by_sha={x['RequestSha256']:x for x in bridge}
requests=sorted((root/'luna-requests').glob('*.request.json'))
responses=root/'luna-responses'; selector=load(root/'selector-replies.json')
orig_reply_hashes=[x['RequestSha256'] for x in selector]
original_calls={}
unit_map=[]; alias_owner={}; phase_counts={}; audits=[]; errors=[]
for p in requests:
 raw=p.read_bytes(); sha=hashlib.sha256(raw).hexdigest(); key=p.name.removesuffix('.request.json')
 if sha!=key: errors.append('REQUEST_FILE_SHA_MISMATCH:'+key)
 req=json.loads(raw); call=bridge_by_sha.get(key)
 if call is None: errors.append('REQUEST_MISSING_BRIDGE_CALL:'+key); continue
 phase=call['Phase']; phase_counts[phase]=phase_counts.get(phase,0)+1
 response_path=responses/(key+'.response.json')
 if not response_path.exists(): errors.append('RESPONSE_FILE_MISSING:'+key); continue
 resp_raw=response_path.read_bytes(); resp=json.loads(resp_raw)
 if resp.get('requestSha256')!=key: errors.append('RESPONSE_SHA_BINDING_MISMATCH:'+key)
 contents=req['messages'][1].get('content',[])
 images=[x for x in contents if x.get('type')=='image_url'] if isinstance(contents,list) else []
 image_evidence=[]
 for block in images:
  url=block.get('image_url',{}).get('url','')
  if not url.startswith('data:image/png;base64,'): errors.append('BAD_IMAGE_URI:'+key); continue
  b=base64.b64decode(url.split(',',1)[1]); h=hashlib.sha256(b).hexdigest()
  entry=next((x for x in image.values() if x['Sha256']==h),None)
  if entry is None: errors.append('WIRE_IMAGE_NOT_RENDERED_PAGE:'+key)
  image_evidence.append({'page':entry['page'] if entry else None,'mimeType':'image/png','sha256':h,'bytes':len(b),'detail':block.get('image_url',{}).get('detail')})
 textblocks=[x.get('text','') for x in contents if x.get('type')=='text'] if isinstance(contents,list) else []
 payload=None
 if phase=='SELECT':
  if not images: errors.append('SELECT_WITHOUT_IMAGE_BLOCK:'+key)
  if len(req['messages'])!=2: errors.append('SELECT_HAS_CROSS_REQUEST_HISTORY:'+key)
  try: payload=json.loads(textblocks[0])
  except Exception: errors.append('SELECT_PAYLOAD_PARSE_FAILED:'+key)
  if payload:
   aliases=[]
   def walk(obj):
    if isinstance(obj,dict):
     if isinstance(obj.get('alias'),str) and re.fullmatch(r'L\d+:S\d+',obj['alias']): aliases.append(obj['alias'])
     for v in obj.values(): walk(v)
    elif isinstance(obj,list):
     for v in obj: walk(v)
   for t in payload.get('targets',[]):
    a=t['alias']; aliases.append(a)
    if a in alias_owner: errors.append('DUPLICATE_TARGET_OWNER:'+a)
    alias_owner[a]=payload['unitId']; unit_map.append({'alias':a,'page':t.get('page'),'unitId':payload['unitId'],'text':t.get('text')})
   if not payload.get('targets'): errors.append('SELECT_NO_TARGETS:'+key)
   target_pages={t.get('page') for t in payload.get('targets',[]) if t.get('page') is not None}
   image_pages={x['page'] for x in image_evidence}
   if not target_pages.issubset(image_pages): errors.append('TARGET_PAGE_IMAGE_OMITTED:'+key)
   if payload.get('stage')!='SEGMENT': errors.append('UNEXPECTED_SELECT_STAGE:'+key)
   orig=call['OriginalTextOnlyRequestSha256']; original_calls[orig]=key
 elif phase in ('GROUP','HIERARCHY_PLACE'):
  orig=call['OriginalTextOnlyRequestSha256']; original_calls[orig]=key
  if images: errors.append('NON_SELECT_WITH_IMAGES:'+key)
  if len(req['messages'])!=2: errors.append('NON_SELECT_HAS_CROSS_REQUEST_HISTORY:'+key)
  if phase=='GROUP' and payload is None and textblocks:
   try: payload=json.loads(textblocks[0])
   except: pass
  if phase=='GROUP' and (payload or {}).get('stage')!='GROUP': errors.append('GROUP_PHASE_CONTENT_MISMATCH:'+key)
 if phase=='SELECT':
  body=json.loads(resp['content']); decisions=body.get('decisions',[])
  tmap={t['alias']:t['text'] for t in (payload or {}).get('targets',[])}
  if len(decisions)!=len(tmap) or {d.get('alias') for d in decisions}!=set(tmap): errors.append('SELECT_DECISION_COVERAGE_MISMATCH:'+key)
  for d in decisions:
   if d.get('decision')=='SELECT' and (d.get('sourceText')!=tmap.get(d.get('alias')) or not d.get('evidenceRefs')): errors.append('SELECT_SOURCE_BOUND_PROOF_INVALID:'+key+':'+str(d.get('alias')))
 audits.append({'phase':phase,'requestSha256':key,'responseSha256':hashlib.sha256(resp_raw).hexdigest(),'responseBoundRequestSha256':resp.get('requestSha256'),
  'originalTextOnlyRequestSha256':call['OriginalTextOnlyRequestSha256'],'finalProviderStylePacketBytes':call['FinalWireBytes'],'imageBlocks':len(images),'imageEvidence':image_evidence,
  'elapsedSeconds':( __import__('datetime').datetime.fromisoformat(call['CompletedUtc'])-__import__('datetime').datetime.fromisoformat(call['StartedUtc'])).total_seconds()})
for h in orig_reply_hashes:
 if h not in original_calls: errors.append('PRODUCTION_SELECT_REPLY_HASH_NOT_IN_WIRE_LOG:'+h)
if len(alias_owner)!=650: errors.append('SCOPED_ALIAS_TOTAL_NOT_650:'+str(len(alias_owner)))
if phase_counts.get('SELECT')!=15: errors.append('SELECT_PACKET_COUNT_NOT_15:'+str(phase_counts.get('SELECT')))
if len(requests)!=22: errors.append('TOTAL_PACKET_COUNT_NOT_22:'+str(len(requests)))
result={'status':'PASS' if not errors else 'FAIL','source':{'pdfSha256':load(root/'run-identity.json')['sourceSha256'],'renderedPageCount':len(image),'renderedPages':[{'page':p,'sha256':x['Sha256'],'bytes':x['byteLength'],'width':x['Width'],'height':x['Height']} for p,x in sorted(image.items())]},
 'providerRequestContract':{'modelRoute':load(root/'run-identity.json')['providerModel'],'freshContext':True,'selectMessageCount':15,'allSelectHaveImageBlocks':sum(1 for a in audits if a['phase']=='SELECT' and a['imageBlocks']>0)==15,'totalActualRequestsCaptured':len(requests),'phaseCounts':phase_counts,
  'groupPlaceTextOnly':not any(a['phase']!='SELECT' and a['imageBlocks'] for a in audits),'externalNetworkSent':False,'transport':'HttpClient invoked test-only custom handler; captured exact final request body before any network send'},
 'scope':{'targetCount':len(unit_map),'uniqueAliasCount':len(alias_owner),'units':sorted(set(alias_owner.values())),'unitCount':len(set(alias_owner.values())),'aliasPageMapping':sorted(unit_map,key=lambda x:(int(x['alias'][1:x['alias'].index(':')]),x['alias']))},
 'bindingAudit':{'requestAndResponseShaBound':not any('SHA' in e for e in errors),'productionTextOnlyRequestHashesMatchCapturedBridge':all(h in original_calls for h in orig_reply_hashes),'pairs':audits},
 'timing':{'providerGenerationLatency':'NOT_MEASURED','observedWallTimeMaxSeconds':max((a['elapsedSeconds'] for a in audits),default=0),'note':'Elapsed durations include evaluator wait time for local response files; they are not model latency.'},
 'errors':errors}
(root/'wire-integrity-audit.json').write_text(json.dumps(result,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({'status':result['status'],'errors':errors,'phaseCounts':phase_counts,'aliases':len(unit_map),'units':result['scope']['unitCount'],'packets':len(audits),'allSelectImages':result['providerRequestContract']['allSelectHaveImageBlocks'],'originalReplyHashMatches':result['bindingAudit']['productionTextOnlyRequestHashesMatchCapturedBridge']}))


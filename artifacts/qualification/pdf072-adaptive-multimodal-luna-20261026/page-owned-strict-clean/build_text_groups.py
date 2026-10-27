import hashlib,json,pathlib
p=pathlib.Path('artifacts/qualification/pdf072-adaptive-multimodal-luna-20261026/page-owned-strict-clean')
freeze=json.loads((p/'frozen-run-manifest.json').read_text(encoding='utf-8'))
atoms=json.loads((p/'fresh-source-atoms.json').read_text(encoding='utf-8'))
by_alias={x['alias']:x for x in atoms}
selected=[]
for packet in freeze['frozenPackets']:
 for alias in packet['selectAliases']:
  selected.append(by_alias[alias])
selected.sort(key=lambda x:x['ordinal'])
input_payload={'stage':'TEXT_ONLY_GROUP_AND_PLACE','sourceDocumentSha256':freeze['documentSha256'],'selectionResponseSha256s':[x['responseBodySha256'] for x in freeze['frozenPackets']],'selectedOccurrences':[{'alias':x['alias'],'ordinal':x['ordinal'],'page':x['page'],'text':x['text']} for x in selected]}
req_bytes=json.dumps(input_payload,ensure_ascii=False,separators=(',',':')).encode('utf-8'); req_sha=hashlib.sha256(req_bytes).hexdigest(); (p/'grouping-input.json').write_bytes(req_bytes)
paired=[['L0000:S0','L0001:S0'],['L0359:S0','L0360:S0'],['L0513:S0','L0514:S0']]
used=set(); groups=[]
for occurrence in selected:
 if occurrence['alias'] in used: continue
 pair=next((x for x in paired if occurrence['alias'] in x),[occurrence['alias']])
 entries=[by_alias[a] for a in pair]; used.update(pair)
 groups.append({'groupId':f'G{len(groups)+1:03d}','placementOrdinal':min(x['ordinal'] for x in entries),'page':entries[0]['page'],'sourceAliases':[x['alias'] for x in entries],'sourceTexts':[x['text'] for x in entries],'text':'\n'.join(x['text'] for x in entries)})
groups.sort(key=lambda x:x['placementOrdinal'])
for i,g in enumerate(groups,1):g['groupId']=f'G{i:03d}';g['placementOrdinal']=i
reply={'status':'COMPLETE','groupingMode':'text-only ordered grouping','groups':groups}
rep_bytes=json.dumps(reply,ensure_ascii=False,separators=(',',':')).encode('utf-8'); rep_sha=hashlib.sha256(rep_bytes).hexdigest();(p/'grouped-final-headings.json').write_bytes(rep_bytes)
bound={'inputSha256':req_sha,'outputSha256':rep_sha,'groupCount':len(groups),'selectedSourceAliasCount':len(selected),'sourceAliasCountInGroups':sum(len(g['sourceAliases']) for g in groups),'responseFile':'grouped-final-headings.json'}
(p/'grouping-bound.json').write_text(json.dumps(bound,ensure_ascii=False,indent=2),encoding='utf-8')
print(f'GROUPED groupCount={len(groups)} aliases={len(selected)} inputSha256={req_sha} outputSha256={rep_sha}')

import hashlib,json,pathlib,re
repo=pathlib.Path('.')
run=repo/'artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02'
source=json.loads((run/'run-identity.json').read_text(encoding='utf-8'))['sourceSha256']
registry_path=repo/'eval/a99-closed-loop/gold-current/registry.v1.json'; registry=json.loads(registry_path.read_text(encoding='utf-8'))
entry=next(e for e in registry['authorities'] if e['sourceSha256']==source)
gold_path=repo/entry['canonicalGoldPath']; sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
if sha(gold_path)!=entry['goldSha256']: raise SystemExit('GOLD_HASH_MISMATCH')
gold=json.loads(gold_path.read_text(encoding='utf-8'))
if gold['authorityId']!='DOC-0252' or gold['source']['sourceSha256']!=source: raise SystemExit('GOLD_IDENTITY_MISMATCH')
freeze=json.loads((run/'pre-gold-freeze.json').read_text(encoding='utf-8')); frozen=json.loads((run/'frozen-decisions.json').read_text(encoding='utf-8')); frozen_sha=sha(run/'frozen-decisions.json')
if freeze['status']!='FROZEN_BEFORE_GOLD' or frozen['status']!='FROZEN_BEFORE_GOLD': raise SystemExit('PREGOLD_FREEZE_REQUIRED')
# Verify freeze hashes before comparing approved Gold.
for f in freeze['files']:
 p=run/f['path']
 if not p.exists() or sha(p)!=f['sha256']: raise SystemExit('PREGOLD_FREEZE_HASH_MISMATCH:'+f['path'])
key=lambda a:(int(re.fullmatch(r'L(\d+):S(\d+)',a).group(1)),int(re.fullmatch(r'L(\d+):S(\d+)',a).group(2)))
unitmap=json.loads((run/'alias-unit-page-map.json').read_text(encoding='utf-8'))
source_ord_to_alias={i:x['alias'] for i,x in enumerate(sorted(unitmap,key=lambda x:key(x['alias'])))}
result=json.loads((run/'native-run-result.json').read_text(encoding='utf-8'))
actual_groups=[]
for el in result['Result']['structure']['elements']:
 if el.get('type')!='Heading': continue
 actual_groups.append(tuple(source_ord_to_alias[s['sourceOrdinal']] for s in el['sources']))
gold_groups=[tuple(part['sourceAlias'] for part in c['sourceParts']) for c in gold['semantic']['claims']]
actual_aliases={a for g in actual_groups for a in g}; gold_aliases={a for g in gold_groups for a in g}
score=lambda expected,predicted:{'tp':len(expected&predicted),'fp':len(predicted-expected),'fn':len(expected-predicted),'precision':len(expected&predicted)/len(predicted) if predicted else 0,'recall':len(expected&predicted)/len(expected) if expected else 0,'f1':2*len(expected&predicted)/(len(expected&predicted)*2+len(predicted-expected)+len(expected-predicted)) if expected or predicted else 1}
actual_g=set(actual_groups);expected_g=set(gold_groups)
out={'authorityId':'DOC-0252','goldSha256':entry['goldSha256'],'registrySha256':sha(registry_path),'sourceSha256':source,'frozenDecisionsSha256':frozen_sha,
 'freezeSha256':sha(run/'pre-gold-freeze.json'),'goldAccessAfterPreGoldFreeze':True,
 'scope':{'sourceAliases':len(unitmap),'goldAliases':len(gold_aliases),'predictedHeadingAliases':len(actual_aliases),'goldGroups':len(expected_g),'predictedGroups':len(actual_g),'targetDecisionCount':frozen['decisionCount'],'selectCount':frozen['selectedAliasCount']},
 'membership':score(gold_aliases,actual_aliases),'exactGroups':score(expected_g,actual_g),
 'falsePositiveAliases':sorted(actual_aliases-gold_aliases,key=key),'missingAliases':sorted(gold_aliases-actual_aliases,key=key),
 'falsePositiveGroups':[list(x) for x in sorted(actual_g-expected_g,key=lambda g:key(g[0]))], 'missingGroups':[list(x) for x in sorted(expected_g-actual_g,key=lambda g:key(g[0]))],
 'goldCapabilities':gold['capabilities'],'placementAudit':{'producedHeadingCount':len(result['Outline']['headings']),'hierarchyAccuracy':'NOT SCORED; Gold capability hierarchyEvaluable is false'},
 'comparisonLimits':['One independently authored response set; not a provider-generated Qwen/Luna inference run.','The test-only adapter captured final provider-style request JSON before network send; no external provider received the calls.','Elapsed bridge timings include evaluator wait and manual response authoring, so no model latency claim is made.','Score is limited to DOC-0252 alias membership and exact source-part groups; Gold does not score hierarchy.']}
(run/'post-freeze-doc-0252-score.json').write_text(json.dumps(out,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({'authorityId':out['authorityId'],'membership':out['membership'],'exactGroups':out['exactGroups'],'scope':out['scope'],'FP':out['falsePositiveAliases'],'FN':out['missingAliases'],'groupFP':len(out['falsePositiveGroups']),'groupFN':len(out['missingGroups'])},indent=2))


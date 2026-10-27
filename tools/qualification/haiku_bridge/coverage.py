import json,glob,os,sys
Q='C:/DocxHeaderExtractor-a99-closed-loop/artifacts/qualification/'
for run in sys.argv[1:]:
    for resp in sorted(glob.glob(Q+run+'/luna-responses/*.response.json')):
        sha=os.path.basename(resp).split('.')[0]
        req=json.load(open(Q+run+'/luna-requests/'+sha+'.request.json',encoding='utf-8'))
        u=req['messages'][1]['content']; t=[p for p in u if p['type']=='text'][0]['text'] if isinstance(u,list) else u
        pk=json.loads(t)
        ans=json.loads(json.load(open(resp,encoding='utf-8'))['content'])
        if pk.get('stage')!='UNIT_SEGMENT':
            print(run[14:17],sha[:8],pk.get('stage') or pk.get('protocolVersion'),'turns',len(req['messages'])); continue
        units={x['unitId'] for x in pk['targetUnits']}
        got=[d.get('unitId') for d in ans.get('decisions',[])]
        sel=sum(1 for d in ans.get('decisions',[]) if d.get('decision')=='SELECT')
        print(run[14:17],sha[:8],'turns',len(req['messages']),'units',len(units),'answered',len(set(got)&units),'extra',len(set(got)-units),'dup',len(got)-len(set(got)),'select',sel)

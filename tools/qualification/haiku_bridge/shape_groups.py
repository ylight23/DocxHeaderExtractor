import json,glob,os,sys
from collections import defaultdict
R=sys.argv[1]
rows=[]
for resp in glob.glob(R+'/luna-responses/*.response.json'):
    sha=os.path.basename(resp).split('.')[0]
    req=json.load(open(R+'/luna-requests/'+sha+'.request.json',encoding='utf-8'))
    u=req['messages'][1]['content']; t=u[0]['text'] if isinstance(u,list) else u
    try: p=json.loads(t)
    except: continue
    if p.get('stage')!='UNIT_SEGMENT': continue
    ans=json.loads(json.load(open(resp,encoding='utf-8'))['content'])
    sel={s['alias'] for d in ans['decisions'] for s in d.get('selected',[])}
    for unit in p['targetUnits']:
        for m in unit['members']:
            f=m.get('facts') or {}; lay=f.get('layout',{}); fo=lay.get('font',{})
            if not fo: continue
            key=(fo.get('name'),fo.get('size'),fo.get('boldRatio',0)>=.5,fo.get('italicRatio',0)>=.5,round(lay['bbox']['left']/2)*2,(f.get('block') or {}).get('lines')==1)
            rows.append((key,m['alias'],m['text'][:50],p['unitId'],m['alias'] in sel))
g=defaultdict(list)
for r in rows: g[r[0]].append(r)
for k,v in g.items():
    s=[r for r in v if r[4]]
    if s and len(s)<len(v) and len({r[3] for r in s})>=1 and len({r[3] for r in v})>1:
        print(len(v),'members',len(s),'selected',k)
        if len(v)<=30:
            for r in v: print('   ',r[1],r[3],'SEL' if r[4] else '---',repr(r[2]))

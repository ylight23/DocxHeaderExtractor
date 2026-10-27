import hashlib, json, re, textwrap
from pathlib import Path

OUT=Path(__file__).resolve().parent
ROOT=OUT.parents[3]
SOURCE=ROOT/"src/DocxHeaderExtractor.HeadingAgent"
manifest=json.loads((OUT/"pre-gold-packet-manifest.json").read_text(encoding="utf-8"))
select_run=json.loads((OUT/"select-run.json").read_text(encoding="utf-8"))
source=json.loads((OUT/"source-units.json").read_text(encoding="utf-8"))
by_alias={r["alias"]:r for r in source["rows"]}
ord_by_alias={r["alias"]:i for i,r in enumerate(source["rows"])}
selected={}
for result in select_run["results"]:
    bound=json.loads((OUT/result["boundFile"]).read_text(encoding="utf-8"))
    for d in bound["content"]["decisions"]:
        if d["decision"]=="SELECT":
            txt=d.get("headingExtent",{}).get("text",d["sourceText"])
            selected[d["alias"]]={"alias":d["alias"],"text":txt,"sourceText":d["sourceText"],"page":by_alias[d["alias"]]["page"],"ordinal":ord_by_alias[d["alias"]],"layout":by_alias[d["alias"]]["block"]}

def extract_raw(path, pattern):
    text=path.read_text(encoding="utf-8")
    m=re.search(pattern,text,re.S)
    if not m: raise RuntimeError(f"prompt constant not found: {path}")
    return textwrap.dedent(m.group(1)).strip("\n")
group_system=extract_raw(SOURCE/"Grouping/PdfBoundaryDecisions.cs",r'internal const string Prompt = """\n(.*?)\n\s+""";')
place_system=extract_raw(SOURCE/"Placement/HeadingPlacer.cs",r'private const string RawSourcePrompt = """\n(.*?)\n\s+""";')
first_req=json.loads((OUT/"requests"/(manifest["packets"][0]["requestSha256"]+".request.json")).read_text(encoding="utf-8"))
instruction=json.loads(first_req["messages"][1]["content"][0]["text"])["instruction"]

# GROUP runs contain only adjacent selected atoms, preserving source order. Every request is fresh/text-only.
ordered=sorted(selected.values(),key=lambda x:x["ordinal"])
runs=[]; current=[]
for item in ordered:
    if current and item["ordinal"]!=current[-1]["ordinal"]+1:
        runs.append(current); current=[]
    current.append(item)
if current: runs.append(current)
run_payload=[]
for i,run in enumerate(runs,1):
    lines=[{"alias":x["alias"],"page":x["page"],"text":x["sourceText"],"selectedText":x["text"],"layout":x["layout"]} for x in run]
    run_payload.append({"run":f"R{i:03d}","lines":lines,"boundaryTargets":[{"leftAlias":run[j]["alias"],"rightAlias":run[j+1]["alias"]} for j in range(len(run)-1)]})
group_payload={"protocolVersion":"PDF_HEADING_BOUNDARIES_V1","stage":"GROUP","instruction":instruction,"documentSha256":source["sourceSha256"],"runs":run_payload}
group_user=json.dumps(group_payload,ensure_ascii=False,separators=(",",":"))
group_req={"model":"qwen/qwen3.7-flash","temperature":0,"max_tokens":8192,"messages":[{"role":"system","content":group_system},{"role":"user","content":[{"type":"text","text":group_user}]}]}

# Fresh local boundary decisions: the document title and wrapped survey title continue across lines.
joins={frozenset(("L0000:S0","L0001:S0")),frozenset(("L0359:S0","L0360:S0"))}
group_reply={"protocolVersion":"PDF_HEADING_BOUNDARIES_V1","runs":[]}
final_groups=[]
for run_entry,run in zip(run_payload,runs):
    boundaries=[]; groups=[[run[0]["alias"]]]
    for left,right in zip(run,run[1:]):
        decision="JOIN" if frozenset((left["alias"],right["alias"])) in joins else "SPLIT"
        reason=("Both source lines form one document or section title." if decision=="JOIN" else "The right source line introduces an independent content unit.")
        boundaries.append({"leftAlias":left["alias"],"rightAlias":right["alias"],"leftText":left["text"],"rightText":right["text"],"decision":decision,"reason":reason})
        if decision=="SPLIT": groups.append([])
        groups[-1].append(right["alias"])
    group_reply["runs"].append({"run":run_entry["run"],"boundaries":boundaries})
    for members in groups:
        pieces=[selected[a] for a in members]
        final_groups.append({"alias":members[0],"members":members,"text":" ".join(x["text"] for x in pieces),"ordinal":min(x["ordinal"] for x in pieces),"page":pieces[0]["page"]})
if sum(len(x["boundaries"]) for x in group_reply["runs"]) != sum(max(0,len(r)-1) for r in runs): raise RuntimeError("group boundary coverage error")

stage_dir=OUT/"text-stages"; stage_dir.mkdir(exist_ok=True)
def write_bound(stage,request,response_content):
    raw_req=json.dumps(request,ensure_ascii=False,separators=(",",":" )).encode("utf-8")
    req_sha=hashlib.sha256(raw_req).hexdigest(); request_path=stage_dir/f"{stage.lower()}-{req_sha}.request.json"; request_path.write_bytes(raw_req)
    content=json.dumps(response_content,ensure_ascii=False,separators=(",",":"))
    raw_resp=json.dumps({"id":f"local-luna-{req_sha[:16]}","object":"chat.completion","model":"local-luna-manual-text","choices":[{"index":0,"message":{"role":"assistant","content":content},"finish_reason":"stop"}],"localAuthoring":{"kind":"fresh_local_luna_response","providerInference":False,"requestSha256":req_sha,"conversationHistory":False}},ensure_ascii=False,indent=2).encode("utf-8")
    resp_sha=hashlib.sha256(raw_resp).hexdigest(); response_path=stage_dir/f"{stage.lower()}-{req_sha}.response.json"; response_path.write_bytes(raw_resp)
    bound={"requestSha256":req_sha,"responseSha256":resp_sha,"requestFile":request_path.name,"responseFile":response_path.name,"content":response_content,"providerInference":False,"conversationHistory":False}
    (stage_dir/f"{stage.lower()}-{req_sha}.bound.json").write_text(json.dumps(bound,ensure_ascii=False,indent=2),encoding="utf-8")
    return {"stage":stage,"requestSha256":req_sha,"responseSha256":resp_sha,"requestBytes":len(raw_req),"responseBytes":len(raw_resp),"requestFile":request_path.name,"responseFile":response_path.name,"boundFile":f"{stage.lower()}-{req_sha}.bound.json","imageBlocks":0}

group_files=write_bound("GROUP",group_req,group_reply)
(OUT/"grouped-headings.json").write_text(json.dumps({"protocolVersion":"PDF_HEADING_GROUPS_V1","kind":"local_fold_of_pairwise_group_reply","headings":final_groups},ensure_ascii=False,indent=2),encoding="utf-8")

# Build PLACE requests from grouped headings and source order; every PLACE call is text-only.
group_aliases={g["alias"]:g for g in final_groups}
parents={}
def parent(child,parent_alias): parents[child]=parent_alias
for a in ["L0004:S0","L0025:S0","L0165:S0","L0271:S0","L0334:S0","L0507:S0"]: parent(a,"ROOT")
parent("L0000:S0","NONE")
parent("L0026:S0","L0025:S0"); parent("L0078:S0","L0025:S0")
for a in ["L0081:S0","L0089:S0","L0107:S0","L0123:S0","L0137:S0","L0150:S0"]: parent(a,"L0078:S0")
parent("L0166:S0","L0165:S0")
for a in ["L0170:S0","L0173:S0","L0181:S0","L0189:S0","L0197:S0","L0203:S0"]: parent(a,"L0166:S0")
parent("L0219:S0","L0165:S0")
for a in ["L0272:S0","L0288:S0","L0315:S0"]: parent(a,"L0271:S0")
parent("L0335:S0","L0334:S0")
for a in ["L0359:S0","L0377:S0","L0400:S0","L0420:S0","L0470:S0"]: parent(a,"L0334:S0")
parent("L0519:S0","ROOT"); parent("L0520:S0","L0519:S0"); parent("L0539:S0","L0519:S0")
for a in ["L0521:S0","L0523:S0","L0526:S0","L0531:S0"]: parent(a,"L0520:S0")
for a,p in [("L0522:S1","L0521:S0"),("L0524:S1","L0523:S0"),("L0527:S1","L0526:S0"),("L0528:S1","L0526:S0"),("L0529:S1","L0526:S0"),("L0532:S0","L0531:S0"),("L0534:S1","L0531:S0"),("L0535:S1","L0531:S0")]: parent(a,p)
parent("L0536:S0","L0520:S0"); parent("L0537:S1","L0536:S0")
parent("L0540:S0","L0539:S0")
for a in ["L0541:S1","L0543:S1","L0545:S1","L0546:S1","L0550:S0"]: parent(a,"L0540:S0")
for a,p in [("L0542:S0","L0541:S1"),("L0544:S0","L0543:S1"),("L0547:S1","L0546:S1"),("L0548:S1","L0546:S1"),("L0551:S1","L0550:S0"),("L0552:S1","L0550:S0"),("L0554:S0","L0553:S0")]: parent(a,p)
parent("L0553:S0","L0539:S0"); parent("L0556:S0","ROOT")
for a in ["L0557:S0","L0566:S0","L0576:S0","L0587:S0"]: parent(a,"L0556:S0")
missing=[g["alias"] for g in final_groups if g["alias"] not in parents]
unknown=[a for a,p in parents.items() if a not in group_aliases or (p not in {"ROOT","NONE","UNRESOLVED"} and p not in group_aliases)]
if missing or unknown: raise RuntimeError(f"placement map invalid missing={missing} unknown={unknown}")

placement_payload={"protocolVersion":"HEADING_PARENT_PROPOSAL_V2_CSHARP_GRAPH_VALIDATION","stage":"HIERARCHY_PLACE","documentSha256":source["sourceSha256"],
 "headings":[{"alias":g["alias"],"text":g["text"],"ordinal":g["ordinal"],"parts":g["members"]} for g in final_groups],
 "documentStyles":{},"measurementsNote":"Geometric measurements in source packet are evidence only.","proposedRelations":[],
 "decidedHeadings":[{"alias":g["alias"],"text":g["text"],"ordinal":g["ordinal"],"parent":parents[g["alias"]]} for g in final_groups],
 "upstreamWithheld":[],"sourceContext":[],"toPlace":[g["alias"] for g in final_groups],"protocolErrors":[]}
place_user=json.dumps(placement_payload,ensure_ascii=False,separators=(",",":"))
place_req={"model":"qwen/qwen3.7-flash","temperature":0,"max_tokens":8192,"messages":[{"role":"system","content":place_system},{"role":"user","content":[{"type":"text","text":place_user}]}]}
place_reply={"protocolVersion":"HEADING_PARENT_PROPOSAL_V2_CSHARP_GRAPH_VALIDATION","placements":[{"alias":g["alias"],"parent":parents[g["alias"]]} for g in final_groups]}
place_files=write_bound("PLACE",place_req,place_reply)
(OUT/"text-stage-manifest.json").write_text(json.dumps({"format":"text-only-group-place-local-luna-v1","selectedAliasCount":len(selected),"contiguousRunCount":len(runs),"groupCount":len(final_groups),"files":[group_files,place_files],"groupSystemSha256":hashlib.sha256(group_system.encode()).hexdigest(),"placeSystemSha256":hashlib.sha256(place_system.encode()).hexdigest()},ensure_ascii=False,indent=2),encoding="utf-8")
print(json.dumps({"selected":len(selected),"contiguousRuns":len(runs),"groups":len(final_groups),"groupBytes":group_files["requestBytes"],"placeBytes":place_files["requestBytes"],"groupBoundaries":sum(len(r["boundaries"]) for r in group_reply["runs"])},indent=2))






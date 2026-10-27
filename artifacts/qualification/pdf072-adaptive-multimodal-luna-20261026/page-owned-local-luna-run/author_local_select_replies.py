import hashlib, json, time
from datetime import datetime, timezone
from pathlib import Path

OUT=Path(__file__).resolve().parent
manifest=json.loads((OUT/"pre-gold-packet-manifest.json").read_text(encoding="utf-8"))
source=json.loads((OUT/"source-units.json").read_text(encoding="utf-8"))
by_alias={r["alias"]:r for r in source["rows"]}

# These are independent visual judgments from the matching inline page image and target list.
whole={
"L0000:S0","L0001:S0","L0004:S0","L0025:S0","L0026:S0",
"L0078:S0","L0081:S0","L0089:S0","L0107:S0","L0123:S0","L0137:S0","L0150:S0",
"L0165:S0","L0166:S0","L0219:S0","L0271:S0","L0272:S0","L0288:S0","L0315:S0",
"L0334:S0","L0335:S0","L0359:S0","L0360:S0","L0377:S0","L0400:S0","L0420:S0","L0470:S0","L0507:S0",
"L0519:S0","L0520:S0","L0521:S0","L0523:S0","L0526:S0","L0531:S0","L0536:S0","L0539:S0","L0540:S0","L0553:S0",
"L0556:S0","L0557:S0","L0566:S0","L0576:S0","L0587:S0",
}
partial={
"L0170:S0": "Treatment ofdual participation economies.",
"L0173:S0": "Productivity adjustmentfactors.",
"L0181:S0": "Subsidized prices.",
"L0189:S0": "Global linking procedure of private education PPPs.",
"L0197:S0": "Construction Wages.",
"L0203:S0": "Reference PPPs.",
"L0522:S1": "Welcome and Opening Remarks",
"L0524:S1": "Update on the ICP 2021 and 2024 Cycles",
"L0527:S1": "Research Topics Emerging from the Previous ICP Cycles",
"L0528:S1": "Forthcoming Research Topics",
"L0529:S1": "Setting Up a Short- and Long-Term Research and Development Agenda",
"L0532:S0": "TAG Composition and Terms of Reference",
"L0534:S1": "Terms of Reference for a Task Force on Annual PPP Production",
"L0535:S1": "Terms of Reference for a Task Force on ICP Classification Update",
"L0537:S1": "The Treatment of Import and Export Prices in International Comparisons",
"L0541:S1": "A Survey Based Approach to Adjustment for Quality Differences in Services in",
"L0542:S0": "International Price Comparisons",
"L0543:S1": "Treatment of Scanner (Transaction) Data in the European Comparison",
"L0544:S0": "Program (ECP)",
"L0545:S1": "Treatment of Negative Expenditures in ICP",
"L0546:S1": "Fine Tuning Global Linking Procedures",
"L0547:S1": "Alternative Global Linking Methods",
"L0548:S1": "Similarity Linking Within and Between Regions",
"L0550:S0": "Improving Reliability of Price Comparisons",
"L0551:S1": "Basic Heading PPPs and Sparsity of Price Tableaus",
"L0552:S1": "Alternative Basic Heading PPP Aggregation Methods",
"L0554:S0": "Any Other Business and Closing",
}

for alias, substring in partial.items():
    if alias not in by_alias or substring not in by_alias[alias]["text"]:
        raise RuntimeError(f"partial heading missing from source: {alias} {substring}")
if whole.intersection(partial) or whole.union(partial).difference(by_alias):
    raise RuntimeError("selection set contains duplicate or unknown aliases")

def utf16len(s): return len(s.encode("utf-16-le"))//2
respdir=OUT/"responses"; respdir.mkdir(exist_ok=True)
select_rows=[]
for n, packet in enumerate(manifest["packets"]):
    req_path=OUT/packet["requestFile"]
    raw_req=req_path.read_bytes()
    req_sha=hashlib.sha256(raw_req).hexdigest()
    req=json.loads(raw_req)
    targets=json.loads(req["messages"][1]["content"][0]["text"])["targets"]
    decisions=[]
    for target in targets:
        alias=target["alias"]
        if alias in whole:
            decisions.append({"alias":alias,"decision":"SELECT","evidenceRefs":[alias],"sourceText":target["text"]})
        elif alias in partial:
            sub=partial[alias]; start=target["text"].index(sub); end=start+len(sub)
            decisions.append({"alias":alias,"decision":"SELECT","evidenceRefs":[alias],"sourceText":target["text"],
              "headingExtent":{"start":utf16len(target["text"][:start]),"end":utf16len(target["text"][:end]),"text":sub}})
        else:
            decisions.append({"alias":alias,"decision":"NOT_SELECT","evidenceRefs":[]})
    if len(decisions)!=len(targets): raise RuntimeError("missing decision")
    content={"protocolVersion":"PDF_SELECT_SOURCE_BOUND_V1","status":"COMPLETE","decisions":decisions}
    content_raw=json.dumps(content,ensure_ascii=False,separators=(",",":"))
    response={"id":f"local-luna-{req_sha[:16]}","object":"chat.completion","model":"local-luna-manual-visual","choices":[{"index":0,"message":{"role":"assistant","content":content_raw},"finish_reason":"stop"}],"localAuthoring":{"kind":"fresh_local_luna_response","providerInference":False,"requestSha256":req_sha,"conversationHistory":False,"packetOrdinal":n+1}}
    raw=json.dumps(response,ensure_ascii=False,indent=2).encode("utf-8")
    resp_sha=hashlib.sha256(raw).hexdigest()
    name=f"{req_sha}.response.json"
    (respdir/name).write_bytes(raw)
    bound={"requestSha256":req_sha,"responseFile":f"responses/{name}","responseSha256":resp_sha,
      "content":content,"localAuthoring":"fresh local vision/text judgment from this packet only","providerLatencySeconds":None,
      "requestBytes":len(raw_req),"responseBytes":len(raw),"packetOrdinal":n+1}
    (respdir/f"{req_sha}.bound.json").write_text(json.dumps(bound,ensure_ascii=False,indent=2),encoding="utf-8")
    select_rows.append({"requestSha256":req_sha,"responseSha256":resp_sha,"page":packet["page"],"part":packet["part"],
      "unitId":packet["unitId"],"requestBytes":len(raw_req),"targetCount":len(targets),
      "selectCount":sum(d["decision"]=="SELECT" for d in decisions),"selectedAliases":[d["alias"] for d in decisions if d["decision"]=="SELECT"],
      "responseFile":f"responses/{name}","boundFile":f"responses/{req_sha}.bound.json","imageSha256":packet["imageSha256"],
      "localConversationHistory":False,"scheduledWave":(n//6)+1})

if set().union(*(set(r["selectedAliases"]) for r in select_rows)) != whole.union(partial):
    raise RuntimeError("response select coverage mismatch")
all_aliases=[a for p in manifest["packets"] for a in p["aliases"]]
if len(all_aliases)!=len(set(all_aliases)) or len(all_aliases)!=650:
    raise RuntimeError("alias packet ownership mismatch")
(OUT/"select-run.json").write_text(json.dumps({"kind":"local-luna-manual-multimodal-contract-run","modelLabel":"local-luna-manual-visual","providerInference":False,"independentRequests":True,"maxSelectConcurrencySchedule":6,"packetCount":len(select_rows),"results":select_rows},ensure_ascii=False,indent=2),encoding="utf-8")
print(json.dumps({"packets":len(select_rows),"selectedAliases":len(whole)+len(partial),"nonselectedAliases":650-len(whole)-len(partial),"decisionCount":sum(r["targetCount"] for r in select_rows)},indent=2))



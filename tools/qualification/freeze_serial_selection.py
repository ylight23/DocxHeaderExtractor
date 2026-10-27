"""Freeze accepted selection/grouping for a controlled placement-only trial. No Gold inputs."""
import json
import sys
from pathlib import Path
from score_serial_calls import boundaries, read

result = read(sys.argv[1])
selected, groups = boundaries(result)
messages = [json.loads(s) for s in result["pipelineAudit"]["rawModelResponses"] if s.startswith("{")]
state = next(m for m in messages if m.get("stage") == "pre-binding-alias-state")
placement = next(m for m in messages if m.get("stage") == "HIERARCHY_PLACE")
packet = json.loads(placement["request"])
extents = {}
for m in messages:
    if "unit" in m:
        extents.update(m.get("headingExtents") or {})
order = {a: i for i, a in enumerate(a for m in messages if "unit" in m and "decisions" in m for a in m["decisions"])}
answers = [s for s in result["pipelineAudit"]["rawModelResponses"] if json.loads(s).get("stage") not in ["HIERARCHY_PLACE", "HIERARCHY_CHAIN_REPAIR"]]
answers.append(json.dumps({"stage": "bound-heading-groups", "groups": sorted(groups, key=lambda g: order[g[0]])}))
answers.append(json.dumps({"stage": "qualification-frozen-selection", "sourceRunId": result["runId"], "liveSelectionCalls": 0}))
fixture = dict(sourceSha256=result["input"]["sha256"], sourceRunId=result["runId"], selection=dict(
    SelectedAliases=sorted(selected, key=order.__getitem__), UnitsComplete=15, UnitsTotal=15,
    UnitFailures=[], ModelAnswers=answers, ProviderCalls=0, ProviderCostUsd=0,
    Placement=dict(DocumentStylesJson=json.dumps(packet["documentStyles"]),
                   StyleJsonByAlias={h["alias"]: json.dumps(h["style"]) for h in packet["headings"] if h.get("style") is not None},
                   MeasurementsNote=packet.get("measurementsNote")),
    Groups=sorted(groups, key=lambda g: order[g[0]]), HeadingExtents=extents,
    AliasStates=state["aliases"], StageSeconds={"selection": 0, "grouping": 0},
    Grouping={}, Resources=dict(ProfileId="qualification-frozen-selection", ByteCap=262144,
                                ConfirmedUsd=0, UnconfirmedUsd=0, Attempts=[])))
fixture["selection"]["UnitsTotal"] = len(result["pipelineAudit"]["ResourcePlan"]["Scopes"])
fixture["selection"]["UnitsComplete"] = fixture["selection"]["UnitsTotal"]
Path(sys.argv[2]).write_text(json.dumps(fixture, indent=2), encoding="utf-8")
print(json.dumps(dict(selectedAliases=len(selected), groups=len(groups), sourceRunId=result["runId"])))

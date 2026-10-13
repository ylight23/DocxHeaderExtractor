"""Freeze the D4.11j/D4.12 owner-trial results (Issue #9, 2026-10-13).

Reads every run summary in the safe store, matches it to its plan (by SHA-256, also in git history), re-hashes every stored raw response
and request, scores the decisions of complete units against the all-heading Gold of corpus documents, and writes one results file.

Nothing in the output is line text: lines are named by alias only. Raw responses and requests stay in the safe store; the file records their
SHA-256 so the stored bytes can be checked later.

usage: python -I tools/p7_f1_v6_d412_freeze.py <repo-root> <safe-root> <out.json>
"""
import glob
import hashlib
import json
import os
import subprocess
import sys

PLAN_DIR = 'artifacts/v5-p7-f1-v6-d4-contractfix/d411'
REGISTRY = 'eval/a99-closed-loop/gold-current/registry.v1.json'


def sha256(b):
    return hashlib.sha256(b).hexdigest()


def num(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return 0.0


def plans_by_sha(repo):
    """Every version of every owner-trial plan file, current and in git history: sha -> (path, commit or None, parsed plan)."""
    found = {}
    for f in glob.glob(os.path.join(repo, PLAN_DIR, 'p7.f1.v6.d4.11j-owner-document-trial-plan.*.v1.json')):
        b = open(f, 'rb').read()
        found.setdefault(sha256(b), (os.path.relpath(f, repo).replace('\\', '/'), None, json.loads(b)))
    commits = subprocess.run(['git', '-C', repo, 'log', '--format=%H', '--', PLAN_DIR], capture_output=True, text=True).stdout.split()
    for c in commits:
        names = subprocess.run(['git', '-C', repo, 'ls-tree', '-r', '--name-only', c, '--', PLAN_DIR], capture_output=True, text=True).stdout.split()
        for n in names:
            if 'owner-document-trial-plan' not in n:
                continue
            b = subprocess.run(['git', '-C', repo, 'show', f'{c}:{n}'], capture_output=True).stdout
            found.setdefault(sha256(b), (n, c, json.loads(b)))
    return found


def gold_by_source(repo):
    reg = json.load(open(os.path.join(repo, REGISTRY), encoding='utf-8'))
    out = {}
    for a in reg['authorities']:
        g = json.load(open(os.path.join(repo, a['canonicalGoldPath']), encoding='utf-8'))
        claims = (g.get('semantic') or {}).get('claims') or []
        if any('sourceParts' not in c for c in claims):
            continue   # a Gold of another shape (DOCX); none of the trial documents uses one
        out[a['sourceSha256']] = (a['authorityId'], {p['sourceAlias'] for c in claims for p in c['sourceParts']})
    return out


def config_of(plan):
    """The settings a plan pins, by the field names the owner-trial plan uses."""
    fh = plan.get('failureHandling') or {}
    return {
        'arm': plan['arms'][0]['arm'] if len(plan['arms']) == 1 else [a['arm'] for a in plan['arms']],
        'promptSha256': {a['arm']: a['promptSha256'] for a in plan['arms']},
        'instructionSha256': plan.get('instructionSha256'),
        'segmentation': (plan.get('segmentation') or {}).get('policy', 'FIXED_48_LINES_V1'),
        'segments': len(plan.get('segments', [])),
        'repetitions': plan.get('repetitions', {}).get('perArmAndSegment'),
        'layoutFactsVersion': plan.get('layoutFactsVersion', 1),
        'jsonObjectAllTurns': 'responseFormatTurns' in plan,
        'fenceUnwrap': 'fenceUnwrap' in plan,
        'hideLayout': 'hideLayoutArm' in plan,
        'fullToolRounds': 'toolRoundsBudget' in plan,
        'transportRetries': fh.get('transportRetries', 0),
        'repairTurn': fh.get('repairTurn', False),
        'maxRepairs': fh.get('maxRepairs', 1 if fh.get('repairTurn') else 0),
        # D4.12r plans pinned three marks in the promptCache text itself; from D4.12s three marks need the separate history-mark field.
        'promptCacheMarks': (3 if 'promptCacheHistoryMark' in plan or 'three marks' in plan['promptCache'] else 2) if 'promptCache' in plan else 0,
        'toolDefinitionsSha256': (plan.get('tools') or {}).get('definitionsSha256'),
    }


def main(repo, safe_root, out):
    plans = plans_by_sha(repo)
    gold = gold_by_source(repo)
    runs = []
    for s in sorted(glob.glob(os.path.join(safe_root, 'run-*', 'run-summary.json')), key=os.path.getmtime):
        run_dir = os.path.dirname(s)
        d = json.load(open(s, encoding='utf-8'))
        path, commit, plan = plans.get(d['planSha256'], (None, None, None))
        # Integrity: every stored response and request still hashes to its name.
        raw_ok, raw_bad, raw_keys = 0, [], []
        for e in d['ledger']:
            k = e.get('RawStoreKey')
            if not k:
                continue
            raw_keys.append(k)
            b = open(os.path.join(run_dir, k + '.bin'), 'rb').read()
            if sha256(b) == k:
                raw_ok += 1
            else:
                raw_bad.append(k)
        req_files = sorted(glob.glob(os.path.join(run_dir, 'requests', '*.json')))
        req_ok = sum(1 for f in req_files if sha256(open(f, 'rb').read()) == os.path.basename(f).split('.')[-2])
        ledger = d['ledger']
        cache = lambda e, k: ((e.get('Cache') or {}).get(k) or {}).get('Value') or 0
        bins = sorted(glob.glob(os.path.join(run_dir, '*.bin')), key=os.path.getmtime)
        units = []
        for u in d['units']:
            dec = u['decisions'] or {}
            units.append({
                'unit': u['UnitId'], 'rep': u.get('Rep'), 'status': u['Status'], 'failure': u.get('Failure') or u.get('IncompleteReason'),
                'posts': u.get('Calls'), 'repairedFrom': u.get('RepairedFrom'), 'repairsUsed': u.get('RepairsUsed', 0),
                'transportRetries': u.get('TransportRetries', 0),
                'modelToolCalls': (u.get('Evidence') or {}).get('ModelToolCallsEmitted'),
                'decided': len(dec), 'selected': [a for a, v in dec.items() if v == 'SELECT'] if u['Status'] == 'COMPLETE' else [],
            })
        status = {}
        for u in units:
            status[u['status']] = status.get(u['status'], 0) + 1
        score = None
        if plan is not None and plan.get('sourceSha256') in gold:
            authority, g = gold[plan['sourceSha256']]
            tp = fp = fn = 0
            for u, raw in zip(units, d['units']):
                if raw['Status'] != 'COMPLETE':
                    continue
                for a, v in raw['decisions'].items():
                    s_, ing = v == 'SELECT', a in g
                    tp += s_ and ing
                    fp += s_ and not ing
                    fn += (not s_) and ing
            p = tp / (tp + fp) if tp + fp else None
            r = tp / (tp + fn) if tp + fn else None
            score = {'goldAuthority': authority, 'goldKind': 'all-heading Gold (not a main-heading Gold)', 'tp': tp, 'fp': fp, 'fn': fn,
                     'precision': round(p, 4) if p is not None else None, 'recall': round(r, 4) if r is not None else None,
                     'f1': round(2 * p * r / (p + r), 4) if p and r else None, 'scope': 'decisions of COMPLETE units only'}
        runs.append({
            'runDirectory': os.path.basename(run_dir), 'arm': d.get('Arm'),
            'planSha256': d['planSha256'], 'planFile': path, 'planFileCommit': commit,
            'codeCommit': plan.get('codeCommit') if plan else None, 'authorityId': plan.get('authorityId') if plan else None,
            'document': plan.get('ownerSupplied', {}).get('fileName') if plan else None,
            'publicCorpus': plan.get('ownerSupplied', {}).get('publicCorpus') if plan else None,
            'config': config_of(plan) if plan else None,
            'parallelism': d.get('parallelism'),
            'posts': d['Calls'], 'providerCostUsd': round(sum(e.get('ProviderCostUsd') or 0 for e in ledger), 6), 'haltReason': d.get('HaltReason'),
            'tokens': {
                'prompt': int(sum(num(e['PromptTokens']) for e in ledger)), 'completion': int(sum(num(e['CompletionTokens']) for e in ledger)),
                'reasoning': int(sum(num(e['ReasoningTokens']) for e in ledger)),
                'cachedRead': int(sum(cache(e, 'CachedTokens') for e in ledger)), 'cacheWrite': int(sum(cache(e, 'CacheWriteTokens') for e in ledger)),
            },
            'wallSecondsFirstToLastResponse': round(os.path.getmtime(bins[-1]) - os.path.getmtime(bins[0]), 1) if len(bins) > 1 else None,
            'unitStatus': status, 'score': score,
            'rawIntegrity': {'responses': len(raw_keys), 'responsesHashOk': raw_ok, 'responsesHashBad': raw_bad, 'requests': len(req_files), 'requestsHashOk': req_ok},
            'rawResponseSha256': raw_keys,
            'units': units,
        })
    result = {
        'artifact': 'p7.f1.v6.d4.12-results.v1',
        'frozenUtc': __import__('datetime').datetime.now(__import__('datetime').timezone.utc).isoformat(timespec='seconds'),
        'issue': 'Issue #9 V6 direct agent, D4.11j owner trials and D4.12 (2026-10-13)',
        'model': 'qwen/qwen3.7-flash via OpenRouter, provider alibaba only (alibaba/us-east-1 ignored), no fallback',
        'contentPolicy': 'no line text: lines are named by alias; raw responses and requests stay in the safe store and are recorded by SHA-256',
        'stoppedRunsWithoutSummary': 'three runs were stopped by the owner or by me before they finished and wrote no summary: the 9-repetition three-arm run, the 5-repetition three-arm run and the 5-repetition single-arm run (their partial raw responses stay in the safe store)',
        'scoringNote': 'scores use the all-heading Gold; the later instructions ask for main headings only, so recall against this Gold understates them; precision and false positives are the comparable figures',
        'runs': runs,
    }
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
        f.write('\n')
    bad = sum(len(r['rawIntegrity']['responsesHashBad']) for r in runs) + sum(r['rawIntegrity']['requests'] - r['rawIntegrity']['requestsHashOk'] for r in runs)
    print(f'{len(runs)} runs frozen; unmatched plans {sum(1 for r in runs if r["planFile"] is None)}; hash mismatches {bad}')


if __name__ == '__main__':
    main(*sys.argv[1:4])

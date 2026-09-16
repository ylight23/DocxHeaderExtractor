# Provider timeout policy v2

Status: PER_ATTEMPT_TIMEOUT_POLICY_PROVEN

- Provider calls: 0
- Gold reads: 0
- Production semantic hash: `5f0eb27dfa44068fc60c69e0dcf8a05b14a061ed7526610e4592d68c268fe5e6`
- Multi-call cumulative-budget test: `True`
- Late-call full-window test: `True`
- Real-hang attempt isolation test: `True`

The proof uses scaled durations for fast deterministic execution; production policy remains 300 seconds per physical attempt with a separate document safety ceiling.

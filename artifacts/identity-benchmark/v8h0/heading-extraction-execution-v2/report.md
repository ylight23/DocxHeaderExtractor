# V8H0 heading extraction execution

Status: **COMPLETED_WITH_FAILURES_PRESERVED**.

Execution used frozen preflight `98ce62e` with retry=0. Gold, identity labels, historical predictions and evaluation artifacts were not read.

- Role calls: **30** / 30
- Pointer calls: **18** / 59 upper bound
- Total calls: **48**
- Frozen bound heading occurrences: **38**

Role output was required to cover every block ID exactly once. Pointer shards were triggered only when their frozen shard contained a valid role-selected heading at confidence >= 0.65. Failures are retained; no retry or response repair was performed.

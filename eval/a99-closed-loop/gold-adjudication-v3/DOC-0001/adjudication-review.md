# DOC-0001 native evaluator-v3 occurrence review

Status: `PENDING_HUMAN_REVIEW`
This packet is a review surface, not Gold. No claim is pre-approved.

## Current authority

- Source: `bench/01-style-chuan.docx` (DOCX)
- sourceSha256: `0ea894862a91f94623fab1de4a0c67b27d4a32fcbd0f41299a1568058d048d14`
- sourceUniverseSha256: `309bfc5571c2eb0567d296c730a8348e6ee2b7460e55e01931fd1841fb8346ba`
- aliases: `14`
- Every current source alias is included in the JSON source universe; hints do not restrict review.

## Human decision contract

Use only: `APPROVE_HEADING`, `REJECT_NOT_HEADING`, `ADJUST_BOUNDARY`, `SPLIT_INTO_MULTIPLE`, `MERGE_AS_COMPOSITE`, `DUPLICATE_OCCURRENCE`, `NEEDS_REVIEW`.
Role is legacy metadata only. Any boundary adjustment must bind to current source text.

## Legacy claims as review hints

### Legacy claim 0

- classification: `EXACT_CURRENT_OCCURRENCE_FOUND`
- historical sourceId/span: `body[1]/p[1]` [0, 24)
- historical semanticRole (non-authoritative): `heading`
- historical raw text:
```text
Chương 1. Quy định chung
```
- current candidate keys: `S0001:0:24`

### Legacy claim 1

- classification: `EXACT_CURRENT_OCCURRENCE_FOUND`
- historical sourceId/span: `body[1]/p[3]` [0, 23)
- historical semanticRole (non-authoritative): `heading`
- historical raw text:
```text
1.1. Phạm vi điều chỉnh
```
- current candidate keys: `S0003:0:23`

### Legacy claim 2

- classification: `EXACT_CURRENT_OCCURRENCE_FOUND`
- historical sourceId/span: `body[1]/p[5]` [0, 22)
- historical semanticRole (non-authoritative): `heading`
- historical raw text:
```text
1.2. Đối tượng áp dụng
```
- current candidate keys: `S0005:0:22`

### Legacy claim 3

- classification: `EXACT_CURRENT_OCCURRENCE_FOUND`
- historical sourceId/span: `body[1]/p[7]` [0, 22)
- historical semanticRole (non-authoritative): `heading`
- historical raw text:
```text
1.2.1. Cơ quan quản lý
```
- current candidate keys: `S0007:0:22`

### Legacy claim 4

- classification: `EXACT_CURRENT_OCCURRENCE_FOUND`
- historical sourceId/span: `body[1]/p[9]` [0, 24)
- historical semanticRole (non-authoritative): `heading`
- historical raw text:
```text
1.2.2. Đơn vị trực thuộc
```
- current candidate keys: `S0009:0:24`

### Legacy claim 5

- classification: `EXACT_CURRENT_OCCURRENCE_FOUND`
- historical sourceId/span: `body[1]/p[11]` [0, 27)
- historical semanticRole (non-authoritative): `heading`
- historical raw text:
```text
Chương 2. Tổ chức thực hiện
```
- current candidate keys: `S0011:0:27`

### Legacy claim 6

- classification: `EXACT_CURRENT_OCCURRENCE_FOUND`
- historical sourceId/span: `body[1]/p[13]` [0, 26)
- historical semanticRole (non-authoritative): `heading`
- historical raw text:
```text
2.1. Phân công trách nhiệm
```
- current candidate keys: `S0013:0:26`

## Current candidate occurrences

### S0001:0:24

- reviewStatus: `PENDING_HUMAN_REVIEW`
- current binding: `S0001` / `body[1]/p[1]` [0, 24)
- page: `n/a`
- legacy classifications: `EXACT_CURRENT_OCCURRENCE_FOUND`
- exact current heading text:
```text
Chương 1. Quy định chung
```
- surrounding source context:
```text
⟦CURRENT⟧Chương 1. Quy định chung⟦/CURRENT⟧
```
- previous source alias: `<none>`
- next source alias: `S0002`

### S0003:0:23

- reviewStatus: `PENDING_HUMAN_REVIEW`
- current binding: `S0003` / `body[1]/p[3]` [0, 23)
- page: `n/a`
- legacy classifications: `EXACT_CURRENT_OCCURRENCE_FOUND`
- exact current heading text:
```text
1.1. Phạm vi điều chỉnh
```
- surrounding source context:
```text
⟦CURRENT⟧1.1. Phạm vi điều chỉnh⟦/CURRENT⟧
```
- previous source alias: `S0002`
- next source alias: `S0004`

### S0005:0:22

- reviewStatus: `PENDING_HUMAN_REVIEW`
- current binding: `S0005` / `body[1]/p[5]` [0, 22)
- page: `n/a`
- legacy classifications: `EXACT_CURRENT_OCCURRENCE_FOUND`
- exact current heading text:
```text
1.2. Đối tượng áp dụng
```
- surrounding source context:
```text
⟦CURRENT⟧1.2. Đối tượng áp dụng⟦/CURRENT⟧
```
- previous source alias: `S0004`
- next source alias: `S0006`

### S0007:0:22

- reviewStatus: `PENDING_HUMAN_REVIEW`
- current binding: `S0007` / `body[1]/p[7]` [0, 22)
- page: `n/a`
- legacy classifications: `EXACT_CURRENT_OCCURRENCE_FOUND`
- exact current heading text:
```text
1.2.1. Cơ quan quản lý
```
- surrounding source context:
```text
⟦CURRENT⟧1.2.1. Cơ quan quản lý⟦/CURRENT⟧
```
- previous source alias: `S0006`
- next source alias: `S0008`

### S0009:0:24

- reviewStatus: `PENDING_HUMAN_REVIEW`
- current binding: `S0009` / `body[1]/p[9]` [0, 24)
- page: `n/a`
- legacy classifications: `EXACT_CURRENT_OCCURRENCE_FOUND`
- exact current heading text:
```text
1.2.2. Đơn vị trực thuộc
```
- surrounding source context:
```text
⟦CURRENT⟧1.2.2. Đơn vị trực thuộc⟦/CURRENT⟧
```
- previous source alias: `S0008`
- next source alias: `S0010`

### S0011:0:27

- reviewStatus: `PENDING_HUMAN_REVIEW`
- current binding: `S0011` / `body[1]/p[11]` [0, 27)
- page: `n/a`
- legacy classifications: `EXACT_CURRENT_OCCURRENCE_FOUND`
- exact current heading text:
```text
Chương 2. Tổ chức thực hiện
```
- surrounding source context:
```text
⟦CURRENT⟧Chương 2. Tổ chức thực hiện⟦/CURRENT⟧
```
- previous source alias: `S0010`
- next source alias: `S0012`

### S0013:0:26

- reviewStatus: `PENDING_HUMAN_REVIEW`
- current binding: `S0013` / `body[1]/p[13]` [0, 26)
- page: `n/a`
- legacy classifications: `EXACT_CURRENT_OCCURRENCE_FOUND`
- exact current heading text:
```text
2.1. Phân công trách nhiệm
```
- surrounding source context:
```text
⟦CURRENT⟧2.1. Phân công trách nhiệm⟦/CURRENT⟧
```
- previous source alias: `S0012`
- next source alias: `S0014`

## Complete source review view

The JSON packet contains every source alias in source order, including aliases without legacy hints or candidate hints.

# Agent harness (đã xóa)

Project `DocxHeaderExtractor.AgentHarness` và skill `skills/heading-extraction` đã được xóa ngày 2026-10-15,
theo quyết định của chủ dự án: CLI và Web chạy thẳng tuyến V6 qua `DocumentRunner`, không còn consent gửi
dữ liệu theo từng run, validator `outline_grounding`/`run_provenance` và guardrail đích ghi.

Thiết kế cũ (guardrail, validator, skill contract, cổng duyệt của người) nằm trong lịch sử git trước
commit gỡ harness. Xem [docs/architecture/current.md](architecture/current.md) để biết kiến trúc hiện tại.

# Kiến trúc agent harness

Hệ thống dùng **một workflow agent có giới hạn**, không dùng một nhóm agent tự trị. LLM là một
tool suy luận trong workflow; code xác định thứ tự bước, quyền truy cập dữ liệu và điều kiện dừng.

```text
CLI / ASP.NET Core Web
          │
          ▼
DocumentAgentHarness                 (application layer)
  1. input_document guardrail
  2. external_data_transfer guardrail
  3. extract_document_headings tool
  4. outline_grounding validator
  5. human_review gate
          │
          ▼
PipelineDocumentExtractionTool       (adapter)
          │
          ▼
HeaderExtractionPipeline             (domain/inference implementation)
  OOXML canonical model → neutral document view → candidates/context
  → local/OpenRouter semantic analyzer → independent critic
  → hierarchy/tree validator → correction → evidence/calibration
```

`skills/heading-extraction/SKILL.md` là chính sách domain được version-control để người phát triển,
benchmark và các integration agent dùng cùng một hợp đồng. Workflow production vẫn được compile
thành code/prompt có test; không đọc và thực thi động một file hướng dẫn tùy ý ở mỗi request.

## Hai biểu diễn, hai trách nhiệm

- `SlimDocument` lấy từ OOXML là canonical document model và source of truth. Nó giữ nguyên
  `index`, `stableId`, document order, paragraph/run/table metadata, numbering, style và span.
- `NeutralDocumentViewSerializer` chiếu model đó thành text + JSON metadata cho LLM. Nội dung
  không được thêm `#`/`##`, vì markup ấy sẽ gợi sẵn đáp án mà hệ thống đang cần dự đoán.
- `SlimXmlSerializer` vẫn tồn tại cho debug/đối chiếu và lệnh `xml`; XML rút gọn không còn là
  prompt production.
- Model chỉ trả source index, semantic role và level. Backend luôn lấy text/span trở lại từ
  canonical model, rồi `OutlineGroundingValidator` kiểm tra trước khi cho đi tiếp.

## Critic và điều kiện dừng

Vòng semantic nằm sát classifier trong Core vì đây là nơi còn đủ block cha/anh em/lân cận để
phản biện có mục tiêu. Harness không retry mù toàn tài liệu. Nó giới hạn step, nhận kết quả đã qua
critic/tree checks, chạy validator source-grounding, rồi kết thúc `Completed` hoặc
`NeedsHumanReview`. Vi phạm ID/cấp/span là lỗi fail-closed, không được biến thành điểm confidence.

## Ranh giới project

- `DocxHeaderExtractor.Core`: đọc DOCX, chunking, classifier, cấu trúc heading, calibration và
  pipeline hiện hữu. Không biết HTTP hay CLI.
- `DocxHeaderExtractor.AgentHarness`: run contract, tool descriptor, guardrail, trace, step budget,
  human-review handoff và adapter cho Core.
- `DocxHeaderExtractor.Cli`: composition root cho dòng lệnh; không gọi pipeline trực tiếp.
- `DocxHeaderExtractor.Web`: ASP.NET Core host/transport; route phát NDJSON nhưng orchestration nằm
  trong harness.
- `DocxHeaderExtractor.Tests`: unit test cả thuật toán Core lẫn hành vi harness.

## Bất biến an toàn

1. Có API key không đồng nghĩa với được gửi dữ liệu. Tool từ xa chỉ chạy khi request đặt
   `AllowExternalDataTransfer=true`.
2. Tool descriptor do code khai báo; model không được tự nâng quyền tool.
3. Run có `MaxSteps`, không có vòng lặp vô hạn hoặc tự retry cả tài liệu.
4. Model không ghi nhãn vàng, không tự thay prompt/code và không tự tăng confidence.
5. Heading chưa qua precision gate hoặc có mâu thuẫn kết thúc ở `NeedsHumanReview`.
6. Trace chỉ chứa stage, trạng thái và số lượng; không ghi nội dung tài liệu.

## Mở rộng đúng hướng

Một tool/stage mới phải có interface rõ, descriptor về rủi ro, guardrail tương ứng và eval độc lập.
Chỉ cân nhắc model-driven routing hoặc multi-agent khi benchmark chứng minh workflow cố định không
đủ, vì mỗi lớp tự chủ mới làm tăng latency, chi phí và bề mặt lỗi.

## Ranh giới n8n

n8n phù hợp cho trigger, lịch chạy, routing tích hợp, notification và approval. Parser, canonical
model, semantic/critic loop, validator, confidence/calibration và test harness vẫn ở codebase này;
không đặt correctness lõi vào node/prompt rời khó version và khó benchmark.

## Tài liệu thiết kế tham khảo

- Microsoft Agent Framework: agent harness hỗ trợ skills và vòng lặp có điều kiện với giới hạn
  số lần: https://learn.microsoft.com/en-us/agent-framework/agents/harness
- Microsoft Agent Skills: workflow phù hợp khi cần hành vi nhiều bước xác định và dự đoán được:
  https://learn.microsoft.com/en-us/agent-framework/agents/skills
- OpenAI Agents SDK runner: agent loop có `max_turns`, guardrails, approvals và tracing:
  https://openai.github.io/openai-agents-python/running_agents/
- MarkItDown mô tả Markdown là biểu diễn text hướng LLM, không thay thế file nguồn:
  https://github.com/microsoft/markitdown

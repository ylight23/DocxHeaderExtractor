# Kiến trúc agent harness

Hệ thống dùng **một workflow agent có giới hạn**, không dùng một nhóm agent tự trị. LLM là một
tool suy luận trong workflow; code xác định thứ tự bước, quyền truy cập dữ liệu và điều kiện dừng.

```text
CLI / ASP.NET Core Web
          │
          ▼
DocumentAgentHarness                 (application layer)
  1. skill contract  (SKILL.md → cấu hình bắt buộc)
  2. plan.tools      (registry chọn tool theo luật code, ghi lý do vào trace)
  3. input_document guardrail
  4. external_data_transfer guardrail
  5. writeback_target guardrail
  6. extract_document_headings tool   ─┐ tối đa maxRepairAttempts lượt
  7. outline_grounding validator      ─┘ (bác → cách ly index → dựng lại)
  8. human_review gate
  9. write_document_outline action     (chỉ khi 7 và 8 đều thông)
          │
          ▼
PipelineDocumentExtractionTool       (adapter)
          │
          ▼
HeaderExtractionPipeline             (domain/inference implementation)
  OOXML canonical model → neutral document view → candidates/context
  → local GGUF/LM Studio/OpenRouter semantic analyzer → independent critic
  → hierarchy/tree validator → correction → evidence/calibration
```

`AgentRunNarrator` dựng câu trả lời cho người từ chính outline đã qua validator và từ trace, nên
CLI, Web và log nói cùng một phiên bản sự thật — kể cả khi sự thật đó là "không ghi được vì còn
6 mục chờ duyệt".

## Skill là hợp đồng, không phải prompt

`skills/heading-extraction/SKILL.md` được nạp lúc chạy và harness fail-closed khi thiếu file.
Nhưng chỉ khối `requires` trong front matter mới có hiệu lực; phần văn xuôi là tài liệu cho người
và **không bao giờ** đi vào prompt — một file hướng dẫn sửa được tự do mà lại chảy thẳng vào
prompt thì chính nó trở thành đường tiêm chỉ thị.

```yaml
requires:
  guardrails: [input_document, external_data_transfer, writeback_target]
  validators: [outline_grounding]
  humanReviewBeforeWriteback: true
  maxRepairAttempts: 1
```

`skill.contract` chạy trước mọi stage khác và chặn run khi cấu hình thực tế không thoả: thiếu một
guardrail/validator được liệt kê, hoặc `MaxRepairAttempts` vượt trần. Nới lỏng chính sách phải đi
qua việc sửa file policy và commit nó — không phải qua một tham số ở call site.
Bộ đọc front matter cố tình hẹp: một tầng, đúng các khoá đã biết, khoá lạ là lỗi.

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

## Vòng sửa: cách ly rồi dựng lại

Validator bác thì harness không hỏi lại model "lần này trả đúng nhé" — với một vi phạm grounding
thì đó là lỗi của code, không phải một ý kiến mà model đổi được. Thay vào đó:

1. Gom các `AgentValidationIssue` có `Index` thành tập cách ly.
2. Gọi lại tool với `AgentRepairFeedback`; `PipelineDocumentExtractionTool` chạy lại pipeline với
   các đoạn đó bị gỡ khỏi tập ứng viên **trước** khi hỏi model — cây, cấp, evidence và cổng
   precision đều được dựng lại, nên một mục bị gỡ không để lại cấp mồ côi.
3. Hết `MaxRepairAttempts`, hoặc tool không khai `SupportsRepair`, hoặc lỗi không quy được về đoạn
   nào (cách ly mù không phải là sửa) → ném `AgentOutputValidationException`. Không hạ chuẩn.

## Hành động ghi

`write_document_outline` là tool đầu tiên có `MutatesExternalState: true`, nên nó đi qua ba lớp:
`writeback_target` guardrail (đích không được trùng nguồn, không đè khi chưa cho phép, thư mục phải
tồn tại), rồi validator, rồi human-review gate — `humanReviewBeforeWriteback` biến gate từ chỗ báo
cáo thành chỗ **chặn** thật. Bản thân việc ghi chỉ đặt `w:outlineLvl` vào bản sao; không sửa một ký
tự nội dung nào, và sau khi ghi phải đọc lại bản đích bằng đúng `DocxSlimExtractor` để đối chiếu
stableId, text và cấp — lệch một mục là xoá file đích và ném lỗi.

Đọc và ghi dùng chung `ParagraphWalker`, vì `index` chỉ có nghĩa khi hai đường đi qua đúng một thứ
tự duyệt. Tách đôi bộ duyệt là cách chắc chắn nhất để writeback đánh dấu nhầm đoạn sau một thay đổi
nhỏ ở một bên.

Host quyết định có nạp tool ghi hay không, và mỗi host có một mô hình đích khác nhau:

- **CLI** chỉ nạp tool ghi khi có `--write-docx <đích>`; đích do người dùng nêu và guardrail kiểm.
- **Web** nạp khi người dùng tick ô tương ứng, nhưng **không bao giờ nhận đường dẫn từ client** —
  đích luôn nằm trong thư mục tạm của chính request đó. Một service nhận file lạ qua HTTP mà lại
  ghi vào đường dẫn do client chọn thì đó là lỗ ghi đè file tuỳ ý. Kết quả trả về qua
  `GET /api/outline/{runId}.docx`, đọc một lần rồi mất; `WritebackStore` chặn trần theo số mục,
  tổng dung lượng và tuổi để tài liệu của người dùng không tồn đọng trong bộ nhớ server.

## Chọn tool: luật của code, ghi lại lý do

`AgentToolRegistry` giữ toàn bộ tool đã đăng ký và chọn ra bộ dùng cho từng run ở stage
`plan.tools`. Luật chọn là code: lọc theo consent (`SendsDataExternally` cần
`AllowExternalDataTransfer`), lấy mức rủi ro thấp nhất, giữ thứ tự đăng ký khi hoà; tool ghi chỉ
vào cuộc khi request nêu đích.

Model không nhìn thấy danh sách tool và không được chọn. Hai câu hỏi mà việc chọn tool quyết định —
dữ liệu có rời khỏi máy không, file có bị ghi không — không nên phụ thuộc vào một chuỗi sinh ra từ
phân phối xác suất. Đổi lại tính tự chủ ấy, mỗi run ghi vào trace tool nào được chọn và vì sao, nên
lựa chọn luôn kiểm tra lại được sau sự việc. Khi không tool nào hợp lệ với consent hiện tại,
registry **không** lặng lẽ đổi sang tool khác: nó giữ nguyên lựa chọn để guardrail chặn đúng lý do,
vì im lặng đổi tool là cách nhanh nhất để một run tưởng là cục bộ hoá ra đã gửi dữ liệu đi.

## Ranh giới project

- `DocxHeaderExtractor.Core`: đọc DOCX, chunking, classifier, cấu trúc heading, calibration và
  pipeline hiện hữu. Không biết HTTP hay CLI.
- `DocxHeaderExtractor.AgentHarness`: policy skill, run contract, tool descriptor, guardrail, trace,
  step/repair budget, human-review handoff, hành động ghi, narrator và adapter cho Core.
- `DocxHeaderExtractor.Cli`: composition root cho dòng lệnh; không gọi pipeline trực tiếp.
- `DocxHeaderExtractor.Web`: ASP.NET Core host/transport; route phát NDJSON nhưng orchestration nằm
  trong harness.
- `DocxHeaderExtractor.Tests`: unit test cả thuật toán Core lẫn hành vi harness.

## Bất biến an toàn

1. Có API key không đồng nghĩa với được gửi dữ liệu. Tool từ xa chỉ chạy khi request đặt
   `AllowExternalDataTransfer=true`.
2. Có quyền ghi thư mục không đồng nghĩa với được ghi. Writeback chỉ chạy khi request nêu đích và
   host đã nạp tool ghi cho đúng run đó.
3. Tool descriptor do code khai báo và việc chọn tool cũng do code; model không nhìn thấy danh
   sách tool, không tự định tuyến và không tự nâng quyền.
4. Run có `MaxSteps` và `MaxRepairAttempts`; không có vòng lặp vô hạn hoặc tự retry cả tài liệu.
5. Model không ghi nhãn vàng, không tự thay prompt/code và không tự tăng confidence.
6. Heading chưa qua precision gate hoặc có mâu thuẫn kết thúc ở `NeedsHumanReview`, và khi ấy
   không hành động ghi nào được phép chạy.
7. Tài liệu nguồn không bao giờ bị sửa; mọi thay đổi đi vào bản sao và phải qua hậu kiểm đọc lại.
8. Đích ghi trên Web do server đặt trong thư mục tạm của request; client không đặt được đường dẫn.
9. Trace chỉ chứa stage, trạng thái và số lượng; không ghi nội dung tài liệu.

## Mở rộng đúng hướng

Một tool/stage mới phải có interface rõ, descriptor về rủi ro, guardrail tương ứng và eval độc lập;
đăng ký nó vào `AgentToolRegistry` là đủ để nó xuất hiện trong `plan.tools` và trong bề mặt quyền
mà `DocumentAgentHarness.Tools` phơi ra.

Chỉ cân nhắc model-driven routing hoặc multi-agent khi benchmark chứng minh workflow cố định không
đủ, vì mỗi lớp tự chủ mới làm tăng latency, chi phí và bề mặt lỗi.

## Ranh giới với vòng lặp agent do model điều khiển

Mô hình "chuẩn" của một agent — danh sách message lớn dần với bốn vai trò `system`/`user`/
`assistant`/`tool`, trường `tools`, và vòng `while` chạy tới khi model thôi xin gọi tool — KHÔNG
phải thứ đang chạy ở đây. Mỗi lần gọi model trong pipeline này chỉ có hai thành phần:

```text
system : luật phân loại + ví dụ one-shot   (tĩnh, ~1098 token, nạp một lần cho cả run)
user   : đúng một khối DOCUMENT_VIEW       (~2,2K token; Qwen 8K dùng ~5K)
```

Không có `tools`. Không có message `assistant`/`tool`. Không có trajectory. Mỗi khối `Fork()` từ
prefix rồi bị huỷ, nên ngữ cảnh không bao giờ lớn dần.

Ba hệ quả, đều là đánh đổi có chủ ý:

1. **Không cần nén ngữ cảnh.** Bài toán "lịch sử phình to thì nén thế nào" không tồn tại ở đây
   theo thiết kế, nên trong repo không có và không nên có code compaction.
2. **Injection không leo thang được.** Trường `tools` rỗng nghĩa là model không có gì để leo thang
   *vào*; cộng với grammar liệt kê thì đầu ra tối đa là vài chữ số sai. Đây là lời giải thích kiến
   trúc cho con số đo ở `07-chen-chi-thi`: bật/tắt câu dặn trong prompt không đổi kết quả, vì cái
   chặn không nằm ở prompt.
3. **Đổi lại, model không xin thêm ngữ cảnh được.** Ranh giới khối do chunker quyết định tĩnh; một
   heading bị cắt rời khỏi cha của nó thì model không có cách nào yêu cầu nhìn rộng ra. Đây chính
   là lý do `--two-pass` tồn tại: đổi mép khối để mỗi ứng viên rơi vào lân cận khác, rồi đánh dấu
   chỗ hai lượt bất đồng.

Nếu sau này muốn chuyển sang vòng lặp do model điều khiển, tool đầu tiên hợp lý là
`expand_context(i, before, after)`. Bốn điều cần nói đúng về nó, vì lập luận "quá tốn kém" nghe
thì tiện nhưng không đứng vững:

1. **Nó KHÔNG phá prefix cache.** `Conversation.Prompt` gọi lại được nhiều lần trên cùng một fork
   mà vẫn giữ KV — vòng sinh token trong `PrefixCachedRunner` đã làm đúng như vậy. Root vẫn cached,
   lượt sau chỉ trả tiền cho phần token mới thêm.
2. **Chi phí tỉ lệ với phần thêm, không phải một khối đầy.** Con số ~55 giây/khối là cho một khối
   2,2K–5K token trên tài liệu 898 đoạn với 7B chạy CPU. Mở rộng vài trăm token cho dăm đoạn mơ hồ
   rẻ hơn hẳn mức đó.
3. **Việc "tập trung lại ngữ cảnh cho mục đáng ngờ" ĐÃ có, do code quyết định.** Lượt critic dựng
   lại một document view chỉ gồm các index đã chọn. Phần chưa có duy nhất là để model tự chọn khi
   nào cần — và đó đúng là phần ít bằng chứng giá trị nhất.
4. **Cái mất thật sự là tính độc lập giữa các khối.** Fork-rồi-huỷ bảo đảm khối 4 không bị nhiễm
   bởi quyết định ở khối 3. Giữ một hội thoại sống qua nhiều lượt là mang phụ thuộc thứ tự trở
   lại — đúng loại lỗi mà repo đã đo được (một dãy `0` kéo chữ số sau về `0`, và `--two-pass` tồn
   tại chính vì đổi mép khối thì câu trả lời đổi theo).

Cộng thêm: model phải tự phán đoán KHI NÀO mình thiếu ngữ cảnh — đúng loại suy luận meta mà mô
hình nhỏ làm kém nhất, trong khi khoảng cách 3B→7B (73% → 97%) cho thấy trần năng lực nằm ở đâu.
Và số lượt gọi thay đổi theo tài liệu làm eval nhiễu thêm, trong khi pipeline vốn đã không tái lập
từng bit. Chỉ đổi khi có benchmark cho thấy lỗi ranh giới khối gây thiệt hại lớn hơn những khoản đó.

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

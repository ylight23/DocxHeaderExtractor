# Giữ cấu trúc, điều chỉnh ngân sách

**Nên triển khai một controller tài nguyên dùng chung, kết hợp adapter phân đoạn riêng cho từng tác vụ.** PDF và scan ưu tiên giữ nguyên trang khi packet đầy đủ vừa giới hạn; DOCX ưu tiên paragraph/container; grouping giữ nguyên selected run; hierarchy chia target nhưng giữ evidence về candidate ancestors. Controller đo body thực, ước lượng token có provenance, cấp output theo schema và đối chiếu tiền/thời gian còn lại trước từng POST. Nó được phép thay kích thước batch, output allowance và concurrency trong envelope đã cấu hình; nó không tự nâng ngân sách tiền, bỏ evidence bắt buộc hay đổi quyết định semantic để làm kết quả đẹp hơn. **Không nên chỉ thay “48 atom” bằng “256 KiB”**, vì như vậy vẫn dùng một con số chung cho các nhu cầu khác nhau. Bằng chứng PDF 072 cho thấy cần bảo toàn request scope, nhưng chưa chứng minh cả trang luôn là scope tốt nhất. Báo cáo ngày **24/10/2026**, dựa trên mã tại baseline `8b1e09a`, artifact qualification và tài liệu/metadata công khai; nghiên cứu này **không sửa production, không gọi inference trả phí, không chạy tests, không sửa Gold**.

## Trang 15 chứng minh scope có tác động, chưa tạo quy luật cho mọi trang

Ở PDF 072, trang 15 có **76 atom** nhưng packer hiện tại chia thành bốn core. Bốn tiêu đề đồng cấp bị phân phối qua các request; raw response có lúc chọn một tiêu đề và loại những tiêu đề khác bằng tiêu chí “nhãn nội bộ của phụ lục”. Replay không tìm thấy C# đảo status: phản hồi model và trạng thái selection/binding tương ứng nhất quán. Trong canary scope, cùng instruction/model/source, packet chứa đủ trang chọn **4/4 peer heading trong cả hai lượt**; cách chia cũ có kết quả **1/4 và 3/4**. Đây là bằng chứng về tác động của scope cùng với variance của model, chưa tách riêng được ảnh hưởng target set, facts coverage, context và kích thước packet. ([Artifact scope](</C:/DocxHeaderExtractor-a99-closed-loop/artifacts/qualification/pdf072-scope-20261023/README.md>))

Full pipeline qualification sau đó thay bốn core `[574,589)`, `[589,621)`, `[621,637)`, `[637,650)` bằng một core `[574,650)`, bảo toàn ownership; tổng core giảm **18 → 15**. Request đầu trang 15 là **103.604 byte**, các request tiếp theo là **112.371, 121.157 và 129.943 byte**. Dư địa dưới cap 131.072 byte giảm từ **27.468 xuống 1.129 byte**; không được lấy dư địa của lượt đầu làm dự phòng của cả conversation. Cả bốn peer được chọn và bind; toàn tài liệu có **42 heading, 26 placed, 2 out-of-hierarchy, 14 unresolved**. Chi phí `$0.031373632` chỉ bao gồm selector/grouping được audit, không phải hóa đơn toàn pipeline vì placement chưa xuất USD. ([Manifest và giới hạn bằng chứng](</C:/DocxHeaderExtractor-a99-closed-loop/artifacts/qualification/pdf072-full-page15-20261023/manifest.json>))

Vì vậy, **“prefer whole page” là policy packing hợp lý cho PDF selection**, còn “page = semantic unit” là khẳng định quá mạnh. Một trang có thể có hai section; một table hoặc section có thể kéo dài qua nhiều trang. Unstructured cũng phân biệt giới hạn mềm/cứng và cho phép section đi qua page break; Docling kết hợp cấu trúc với tokenizer trước khi split/merge. Đây là precedent kỹ thuật, chủ yếu cho chunking/RAG, chưa phải chứng minh chất lượng heading extraction của repository. ([Unstructured](https://docs.unstructured.io/open-source/core-functionality/chunking), [Docling](https://docling-project.github.io/docling/concepts/chunking/))

Context lớn hơn cũng không tự bảo đảm reasoning tốt hơn. *Lost in the Middle* và *RULER* cho thấy chất lượng phụ thuộc vị trí evidence, độ dài và loại tác vụ; chúng không cung cấp một ngưỡng token phổ quát cho Qwen/heading extraction. Hard context window nói về khả năng nhận input, còn soft quality target cần qualification riêng. ([Liu et al.](https://arxiv.org/abs/2307.03172), [Hsieh et al.](https://arxiv.org/abs/2404.06654))

## Cần sửa phép đo và ledger trước khi gọi hệ thống là thích ứng

Repository đã có nền tảng tốt: `PreparedRequest` giữ body, số UTF-8 byte và SHA256; transport kiểm tra hash trước gửi. Có thể đo rồi gửi **đúng cùng body**, thay vì dựng một packet gần giống để ước lượng. Vấn đề là nhiều quyết định phía trên vẫn dựa trên số atom hoặc compact facts, còn budget dùng byte làm surrogate token và reserve cố định. ([OpenRouterWire.cs:42](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.Agents/OpenRouterWire.cs:42), [AgentModel.cs:113](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.Agents/Transport/AgentModel.cs:113))

| Quan sát trong code hiện tại | Hệ quả và thay đổi cần làm |
|---|---|
| PDF giới hạn core 48 dòng rồi còn chia theo facts bytes; DOCX 48 paragraph/28.000 facts bytes; scan paragraph packing cũng 48 dòng. [PDF:17](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Formats/AgentSource.cs:17), [DOCX:41](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Formats/DocxAgentSource.cs:41), [scan:34](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Scan/ScanAgentSource.cs:34) | Chưa đo system/tools/styles/context/history của toàn request. Thay bằng planner gọi cùng request builder thật; số atom chỉ còn là feature dự báo output. |
| `PromptTokenBudget` so với `request.Bytes`; `AttemptUsd` chọn `MinPromptTokens` tier bằng request bytes và reserve output 32.768 cho mọi call. [AgentBudget.cs:11](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.Agents/Budget/AgentBudget.cs:11), [AgentBudget.cs:53](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.Agents/Budget/AgentBudget.cs:53) | Đơn vị không khớp. Tách byte cap, input-token estimate, output cap và pricing tier; surrogate bảo thủ phải ghi rõ là surrogate, không gọi là actual tokens. |
| Scan admit tới 262.144 byte, nhưng spare-call planner vẫn dựa 131.072; grouping reserve dùng 60.000 byte/chunk. [ScanSelector.cs:166](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Scan/ScanSelector.cs:166), [AgentSelector.cs:134](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Selectors/AgentSelector.cs:134) | Ceiling dự kiến chưa đi theo cap thực. Reserve phải sinh từ request/output policy của stage và toàn call tree. |
| Hierarchy hỏi `max(256,96+128×targets)`, tức 2.144 token cho 16 rows, nhưng shared reserve vẫn tính 32.768 completion token. [HeadingPlacer.cs:80](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Placement/HeadingPlacer.cs:80) | Có thể từ chối quá sớm vì reserve khác request. Ledger phải dùng enforced cap đúng body, không một hằng số toàn hệ thống. |
| `RecordActual` cộng actual nhưng không settle/release cumulative reserve. [AgentBudget.cs:43](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.Agents/Budget/AgentBudget.cs:43) | Đây là pessimistic cumulative ceiling; chưa là reusable reservation ledger. Chỉ hoàn phần dư khi billing outcome đã biết. |
| Tool conversation dựng lại toàn history mỗi turn; tool content cap 65.536 byte không bảo đảm body hoàn chỉnh luôn vừa. [ToolConversation.cs:67](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.Conversation/ToolConversation.cs:67), [AgentProtocol.cs:24](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Protocol/AgentProtocol.cs:24) | Cần admission lại ở mọi POST, dự phòng continuation và paging contract cho tools. |
| Usage parser so provider prompt tokens với request bytes; token counters chưa đi xuyên safe audit, placement thiếu billing. [Usage.cs:29](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.Agents/Transport/Usage.cs:29), [RunAudit.cs:63](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.DocumentProcessing/Audit/RunAudit.cs:63) | Không thể hiệu chỉnh chính xác hoặc báo tổng run USD đầy đủ. Xuất per-attempt usage/route/cost/status trước khi học tự động. |

Các chiều tài nguyên cần độc lập trong kiểu dữ liệu và audit. **Không có một con số request-size nào thay thế được tất cả các chiều này.**

| Chiều | Trần cứng cần cấu hình hoặc discovery | Mục tiêu mềm | Phần được tự điều chỉnh |
|---|---|---|---|
| Body UTF-8 bytes | Provider/proxy limit nếu biết; operator memory/transport ceiling | Kích thước thuận tiện, continuation headroom | Giữ/gộp/chia legal scopes trong envelope |
| Input/context tokens | Endpoint max prompt và context | Quality target theo task | Optional evidence, batch scope; required evidence giữ nguyên |
| Completion/response | Endpoint output cap, response byte guard, schema coverage | Visible output và reasoning dự kiến | Output allowance theo cardinality/phase và telemetry |
| USD/attempts/deadline | Run ceiling được authorize | Phân bổ giữa stages và reserve work còn lại | Chuyển allowance giữa scopes nếu parent ledger cho phép |
| RPM/TPM/concurrency | Quota thực và local resource limit | Latency/fairness | Queue, backoff, in-flight permits |

**Người vận hành vẫn phải quyết định tiền tối đa, deadline, model/provider được phép và resource ceiling.** Controller không tự suy ra mức tiền người dùng sẵn lòng trả từ tài liệu. Khi metadata thiếu, giá trị là unknown; không đổi null thành unlimited hoặc cost 0. Một atomic unit vô hạn hoặc tool history không bound là phản ví dụ cho lời hứa “luôn giữ trang, luôn hoàn thành, luôn trong ngân sách”.

## Profile endpoint xác định giới hạn, usage xác định phần đã tiêu

Metadata OpenRouter public được fetch lúc **00:02:25 ngày 24/10/2026, Asia/Saigon** (`2026-10-23T17:02:25.9273228Z`) cho `qwen/qwen3.7-flash`: hai endpoint `alibaba` và `alibaba/us-east-1` đều có context **1.000.000**, max prompt **983.616**, max completion **65.536 token**. Tokenizer family là `Qwen`; tools, reasoning, response_format và max_tokens được quảng bá hỗ trợ. Endpoint Alibaba cơ sở có giá USD/M token input/output **0,03/0,13**, từ 32.000 input token **0,10/0,40**, từ 256.000 **0,20/0,80**; US endpoint có giá khác. Đây là snapshot catalog, không bảo đảm route hay giá sẽ bất biến. ([API trực tiếp](https://openrouter.ai/api/v1/models/qwen/qwen3.7-flash/endpoints), [snapshot JSON](</C:/DocxHeaderExtractor-a99-closed-loop/research_notes/Phân đoạn theo ngân sách/openrouter-qwen37-endpoints.json>), [timestamp và SHA256](</C:/DocxHeaderExtractor-a99-closed-loop/research_notes/Phân đoạn theo ngân sách/openrouter-qwen37-endpoints.fetch.json>))

Alibaba direct công bố max output 131.072 cho model này, khác OpenRouter 65.536. **Ứng dụng route qua OpenRouter phải theo endpoint OpenRouter được dùng**, không nhập khả năng direct làm hard guarantee. Nếu allow fallback qua nhiều endpoint, planner dùng intersection giới hạn/capability hoặc quyết định route hợp lệ trước khi đo. Pin model ID mà vẫn cho route sang endpoint khác chưa đủ để pin accounting. Profile cần endpoint tags, source URL/hash, fetchedAt, TTL, pricing/tokenizer revisions; actual route phải quay về audit. ([Alibaba model info](https://www.alibabacloud.com/help/en/model-studio/qwen3-7-flash), [OpenRouter endpoints schema](https://openrouter.ai/docs/api/api-reference/endpoints/list-all-endpoints-for-a-model), [provider routing](https://openrouter.ai/docs/guides/routing/provider-selection))

Nghiên cứu chưa tìm thấy tài liệu chính thức quy định **128/256 KiB là HTTP body ceiling chung của OpenRouter**. Đây đang là cap ứng dụng; có thể nâng trong envelope đã cho phép. Không tìm thấy cap công khai cũng không đồng nghĩa unlimited: proxy, upstream và media có thể có giới hạn riêng. Tương tự, chưa xác nhận một preflight token-count endpoint tổng quát cho chat/tools/images của route Qwen này; không nên dựng một API tưởng tượng hoặc gọi inference chỉ để “đếm miễn phí”.

Trước POST, exact là body bytes/hash, aliases, image dimensions và enforced generation cap. Native input tokens chỉ là estimate nếu tokenizer revision/chat template/tool/image preprocessing chưa khớp. Sau response mới có native prompt/completion/reasoning/cache usage và cost nếu provider báo. OpenRouter usage bao gồm tools và multimodal input; tokenizer family `Qwen` không đủ để gọi local counter là exact. Không dùng `bytes/4`, ký tự hay base64 length như token truth. Counter cần trả estimate/upper bound có method, uncertainty và revision, rồi hiệu chỉnh bằng reported usage. ([OpenRouter response schema](https://openrouter.ai/docs/api_reference/overview), [Alibaba model usage](https://www.alibabacloud.com/help/en/model-studio/model-usage-statistics))

Output demand phụ thuộc tác vụ: selection phải trả một status cho **mọi target**, kể cả NOT_SELECT; grouping có thể tạo một group cho mỗi alias; hierarchy trả edge rows; transcription cần text và tọa độ chưa biết trước. Schema string/array chưa có max thì không có finite schema upper bound để tuyên bố. Có thể đo fixture và phân phối output/item để dự báo, nhưng không biến dự báo thành proof. Trên đa số provider, reasoning và visible output cùng ăn `max_tokens`; cần adapter xác nhận semantics cụ thể, cộng reasoning **một lần**, không trừ/cộng trùng. `reasoning.exclude` chỉ giấu reasoning, không xóa token/phí. Completion cap quá lớn còn làm tăng in-flight spending reservation, nên “cứ xin tối đa” cũng có giá vận hành. ([Reasoning tokens](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens), [OpenRouter limits](https://openrouter.ai/docs/api_reference/limits))

Ledger mới cần trạng thái **confirmed spent, provisional unknown, outstanding reservations** theo attempt ID. Check+reserve call/token/USD atomically trước dispatch; sau usage settlement mới release phần dư, idempotent để không refund hai lần. Hủy trước gửi và hủy sau gửi khác nhau: tài liệu OpenRouter liệt kê **Alibaba không hỗ trợ upstream stream cancellation**, nên abort kết nối không bảo đảm ngừng sinh hoặc ngừng billing. Giữ charge/reserve provisional khi timeout hoặc mất final usage; reconcile generation stats theo bounded policy. Late result có thể cập nhật accounting nhưng không được writeback khi cancellation lease đã thu hồi. ([Streaming và cancellation](https://openrouter.ai/docs/api_reference/streaming), [usage accounting](https://openrouter.ai/docs/cookbook/administration/usage-accounting))

Parent run ledger phải bao phủ vision/transcription, selection, grouping, mọi auxiliary audit/review thực được enable, hierarchy và transport retries; local OCR góp CPU/time chứ không provider USD. Stage allowance ngăn selection tiêu hết phần còn cần cho grouping/placement. Rate/concurrency là controller riêng: 429 dẫn tới backoff/jitter và giảm in-flight, không phải lý do cắt trang. Không có quota direct Alibaba nào chứng minh quota OpenRouter account hiện tại. Provider context compression có thể cắt messages giữa; exact extraction không nên bật transform kiểu này làm cách “tự vừa context”. ([OpenRouter limits](https://openrouter.ai/docs/api_reference/limits), [message transforms](https://openrouter.ai/docs/guides/features/message-transforms), [.NET limiters](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-9.0))

## Controller dùng chung, ranh giới chia và ghép thuộc về từng task

Controller chỉ quản lý tài nguyên. Selection vẫn sở hữu membership; grouping chỉ đề xuất boundaries; parent proposal qua deterministic graph validation. **Không phục hồi semantic verifier authority**, không hardcode Chương/Điều/Annex hay mẫu `2./a)`, không dùng model confidence để quyết định chất lượng partition. Legal cuts xuất phát từ source representation và task contract.

| Task | Scope ưu tiên và điểm được chia | Điều kiện ghép kết quả |
|---|---|---|
| PDF selection | Page → parser block → visual row → atom nếu adapter cho phép | Mọi alias đúng một owner; context read-only có thể lặp; source order, facts, extent mapping giữ nguyên |
| Scan selection | Page/layout paragraph; reading/extent và crop identity giữ nguyên | Như PDF, thêm OCR/view/bbox provenance; không giảm resolution âm thầm để vừa cap |
| DOCX selection | OOXML paragraph, table row/container; không bịa physical page | Không chẻ paragraph mất formatting hoặc UTF-16 extent coordinates; fragment chỉ khi có mapping contract |
| Grouping | Một contiguous selected run mặc định atomic; có thể batch nhiều run độc lập | Không chia run rồi nối JSON đơn thuần: có thể mất group băng qua cut. Cần boundary-overlap/composition contract trước khi cho split |
| Hierarchy | Batch target edges, kèm candidate ancestors/catalog và source evidence cần thiết | Chia targets không được làm mất parent đúng; validate exact coverage, parent identity/order, DAG rồi resolver |
| OCR/transcription/vision | Page hoặc crop theo reading order, có overlap/transform map | Ownership theo region/source occurrence; dedup theo text đơn thuần có thể xóa occurrence lặp thật |
| Tool evidence | Slice/paging cursor có max result size | Giữ call/result pairs; phân biệt returned và đã được đưa vào POST tiếp theo |

Các số hiện tại như grouping tối đa 8 run/request, OCR/transcription line batch hay hierarchy 16 targets là defaults riêng cần migrate theo task, không xóa tất cả cùng lúc. Selected reading và heading extent đang được forward đến grouping phải tiếp tục là nguồn text; packing không được quay lại raw full line khi selection đã chọn alternate reading/substring. ([AgentGrouping.cs:252](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Grouping/AgentGrouping.cs:252), [PdfFormat.cs:43](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Formats/PdfFormat.cs:43), [ScanFormat.cs:35](/C:/DocxHeaderExtractor-a99-closed-loop/src/DocxHeaderExtractor.HeadingAgent/Scan/ScanFormat.cs:35))

Một contract tối thiểu đề xuất là:

```csharp
record ResourceEstimate(long WireBytes, TokenRange InputTokens,
    int RequestedCompletion, int EnforcedCompletion,
    decimal ReservationUsd, string Method, string ProfileId);

interface IAdaptiveDocumentTask {
    NaturalUnitTree NaturalUnits(SourceSnapshot source);
    EvidenceScope RequiredEvidence(TargetScope targets);
    OutputDemand EstimateOutput(TargetScope targets, Calibration stats);
    PreparedRequest Prepare(TargetScope targets, EvidenceScope evidence,
        ConversationState history, int completionCap);
    SplitResult SplitAtLegalBoundary(TargetScope targets, FailureClass cause);
    bool CanCompose(IReadOnlyList<TargetScope> children);
}
```

`NaturalUnitTree` cung cấp hierarchy và cut penalties; `EvidenceScope` đánh dấu required/optional; `TokenRange` giữ estimator revision và confidence; `PreparedRequest` bất biến sau finalize. Nếu transport thay model/max_tokens/history sau đo thì phải đo lại. Admission bảo thủ đề xuất kiểm tra đồng thời:

```text
wireBytes <= min(knownProviderBodyLimit, operatorBodyCeiling)
inputUpper <= endpoint.maxPromptTokens
completionEnforced <= endpoint.maxCompletionTokens
inputUpper + completionEnforced + uncertaintyMargin <= contextWindow
completionEnforced >= task.minimumOutputDemand
confirmed + provisional + outstanding + reservation <= run.maxUsd
attempt, deadline, rate và concurrency permits đều hợp lệ
```

Đây là **policy thiết kế**, không phải mô tả chính xác server reservation nội bộ Qwen. Khi enforced output thấp hơn minimum demand, request không admissible hoặc phải chia hợp lệ; không clamp im lặng rồi gửi một request dự kiến thiếu coverage.

Planner bản đầu nên dùng ordered greedy với legal boundaries và bounded lookahead. Freeze source/alias/evidence snapshots, task/schema/instruction, model profile và calibration revision; thử natural unit nguyên vẹn, dựng body đầy đủ, đo rồi admit. Nếu không vừa, phân loại chiều đang thiếu; giảm optional evidence theo contract hoặc xuống legal child scopes; atomic unit không vừa thì báo `UNSPLITTABLE_REQUIRED_SCOPE`. Có thể merge các phần nhỏ liền nhau nếu `CanCompose`; không reorder các đoạn xa nhau chỉ để tiết kiệm POST. Cuối cùng chứng minh ownership union bằng universe, missing/duplicate bằng 0 và ranges đúng source order.

Không nên binary-search điểm cắt trước khi chứng minh monotonic: mở rộng targets có thể chuyển alias từ context sang target, thay dedup/style/catalog và làm body thay đổi không đơn điệu; tokenization chuỗi nối cũng không cộng tuyến tính. Enumerate/cache một tập legal endpoints hữu hạn phù hợp hơn cho bản đầu; dùng additive estimate để prune nhanh, còn final admission luôn đo body thật.

Mỗi continuation sau tool/repair dựng và đo lại toàn request. Nếu vượt, chỉ paging evidence hoặc khởi tạo fresh child scope khi task chứng minh preservation/composition; không âm thầm xóa tool history bắt buộc hay dùng LLM summary thay source authority. Nếu `finish=length`, không authorize prefix dù parse được; bounded retry chỉ khi tăng output hoặc giảm scope giải quyết nguyên nhân. Malformed coverage nhận protocol feedback cụ thể; 429 nhận backoff; semantic result hợp lệ nhưng sai được đánh giá offline, không để controller tự retry đến khi thích kết quả.

## Migrate PDF trước, học ngân sách sau khi accounting đáng tin

Migration nên bắt đầu ở tầng **measurement và accounting**, tiếp theo mới đổi packing. Bổ sung per-attempt actual usage/route/finish, requested/enforced output, exact first/max continuation bytes, provisional billing và ledger parent-child. Tách helper dựng first request cho arbitrary scope khỏi constructor selection; mở hook `Prepare/MeasureNextTurn` trước admission của conversation loop hiện có. Những thay đổi này tận dụng `PreparedRequest` thay vì viết lại transport, đồng thời giữ cancellation/quarantine và parser fail-closed.

Sau đó chạy **offline shadow PDF planner** trên source snapshots đã có: thử page nguyên vẹn, tính required evidence/output, legal split và ownership proof. Page 15 là regression bắt buộc về giữ scope khi vừa ngân sách; thêm PDF nhiều cột, trang rất dày, table/list qua trang và Unicode facts lớn để đo generalization. Paired qualification predeclare cohort/order, pin model/instruction/source và lặp để thấy variance. Production PDF bật theo policy version có rollback flag; giữ semantic instruction/parser/binding ổn định để biết chính packing đã thay gì. Tổng placed không phải gate duy nhất và hierarchy unresolved không tự phủ nhận cải thiện selector.

DOCX/scan selection migrate tiếp bằng adapter riêng. Hierarchy có thể điều chỉnh batch/output khi candidate-evidence contract được giữ; grouping run và OCR crop chỉ migrate sau khi có composition contract thực sự. Không chia grouping run để “cứu” budget rồi giả định các child valid đồng nghĩa whole run đúng. Toàn call tree phải có allowance cho những auxiliary stages thực được enable; audit-only pass không tự có quyền đổi kết quả production.

**Adaptive learning là bước cuối.** Calibrate theo task/schema/model/provider/tokenizer/modality/reasoning mode, không trộn OCR ảnh với text grouping. Có thể dùng quantile bảo thủ/EWMA của input-count error, output/item, reasoning share, tool growth và latency, kèm sample count, min/max, hysteresis. Hai canary không đủ chọn một P95 đáng tin; cold start dùng default versioned. Completion bị `length` là mẫu censored: nhu cầu thực ít nhất bằng cap, không phải đúng bằng cap. Freeze calibration trong một run hoặc ghi mọi revision để scheduling không làm mất khả năng replay.

Safe audit cần `PlanId`, source/evidence/universe hashes, task/policy/profile/calibration revisions, `ParentUnitId`, split cause và mỗi POST `RequestSha256`. `SEG-015` chỉ là chỉ số, không phải identity ổn định sau replan. Có thể durable-export aliases, scope/resource metadata và hashes mà không bắt người chạy bật raw prompt; raw body/source cần policy lưu riêng. Metrics vận hành gồm exact coverage 100%, missing/duplicate 0, protocol completeness, truncation, retries, first/max bytes/tokens, actual/provisional/reserved USD, latency p50/p95, rate errors, page/block cuts và context duplication. Metrics chất lượng phải thêm peer consistency, selection precision/recall, extent correctness, false merge/split và wrong-parent edges; adjudication cục bộ không được đổi tên thành Strict Gold.

Kết quả triển khai mong muốn là **người dùng đặt envelope một lần, từng task tự chọn scope và allowance phù hợp bên trong envelope đó**. Nó không hứa “tự tối ưu chất lượng” từ hóa đơn hay JSON hợp lệ. Giá trị của controller là bảo toàn cấu trúc và evidence khi còn tài nguyên, chỉ chia ở ranh giới có contract, và nói rõ lý do dừng khi không thể tiếp tục. Với repository hiện tại, PDF là điểm vào có bằng chứng tốt nhất; accounting đúng đơn vị và mọi-turn admission là điều kiện để mở rộng cách làm này sang các tác vụ khác một cách kiểm soát được.

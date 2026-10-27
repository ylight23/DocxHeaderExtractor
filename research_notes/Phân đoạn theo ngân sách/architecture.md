# Kiến trúc phân đoạn và ngân sách thích ứng cho tác vụ tài liệu

Ghi chú nghiên cứu ngày 24/10/2026. Chỉ đọc mã và tài liệu công khai; không gọi provider trả phí, không đổi production, không sửa Gold. Mọi contract/thuật toán ở phần Inferences là đề xuất kỹ thuật, chưa phải behavior đã qualify.

## 1. Có thể thay số atom cố định bằng cách giữ cấu trúc và đo ngân sách thật không?

### Takeaway

Có. Nên tách cấu trúc tài liệu, đóng gói request và admission tài nguyên thành ba việc riêng: tác vụ cung cấp đơn vị tự nhiên cùng các vị trí được phép cắt; controller đo request hoàn chỉnh rồi quyết định giữ, gộp hoặc chia. Nhưng trang nguyên vẹn chỉ là đơn vị ưu tiên hợp lý cho PDF/scan, không phải lời bảo đảm mọi trang là một đơn vị ngữ nghĩa hoặc càng nhiều context càng chính xác.

### Cited Findings

- Docling phân biệt chunk với cách serialize kèm metadata; HybridChunker bắt đầu từ cấu trúc phân cấp, tách phần vượt giới hạn tokenizer, rồi có thể gộp các phần kế tiếp có cùng heading/caption. Đây là một precedent thực cho structure-first + token-aware refinement; chức năng chủ yếu phục vụ embedding/RAG, không phải bằng chứng trực tiếp về extraction heading trong repository này. — [Docling chunking](https://docling-project.github.io/docling/concepts/chunking/).
- Unstructured chunk trên document elements, giữ nguyên element nếu nó tự vừa giới hạn, chỉ text-split element quá lớn; phân biệt hard maximum với soft target. `by_title` có tùy chọn để section kéo dài qua trang. Overlap giữa các chunk vốn đã có ranh giới sạch còn có thể gây nhiễu. — [Unstructured chunking](https://docs.unstructured.io/open-source/core-functionality/chunking).
- Lost in the Middle cho thấy vị trí evidence trong context có ảnh hưởng mạnh trên QA nhiều tài liệu và key-value retrieval, ngay cả với model long-context. Kết quả không đủ để gán một ngưỡng token phổ quát cho heading extraction. — [Liu et al., 2023](https://arxiv.org/abs/2307.03172).
- RULER mở rộng việc đánh giá long context sang tracing và aggregation; model được thử nghiệm có thể suy giảm khi tăng độ dài và độ phức tạp dù đạt tốt trên needle retrieval đơn giản. — [Hsieh et al., 2024](https://arxiv.org/abs/2404.06654).
- Tài liệu Claude tính system, messages, tools, tool results, ảnh/tài liệu và output vào context theo quy tắc model. Vì vậy kích thước source text hoặc facts riêng lẻ không đại diện cho toàn request. — [Context windows](https://platform.claude.com/docs/en/build-with-claude/context-windows).
- Code hiện tại của repository: PDF vẫn dùng `Unit=48`, sau đó còn cắt theo kích thước compact facts; DOCX dùng 48 paragraph/28.000 facts bytes; scan paragraph packing vẫn trần 48 line. `PreparedRequest` đã serialize một lần và giữ `Body`, `Bytes`, `Sha256`, là điểm tốt để đo rồi gửi đúng cùng body. — [PDF AgentSource](../../src/DocxHeaderExtractor.HeadingAgent/Formats/AgentSource.cs), [DOCX AgentSource](../../src/DocxHeaderExtractor.HeadingAgent/Formats/DocxAgentSource.cs), [scan source](../../src/DocxHeaderExtractor.HeadingAgent/Scan/ScanAgentSource.cs), [OpenRouterWire](../../src/DocxHeaderExtractor.Agents/OpenRouterWire.cs).

### Inferences

**Đơn vị tự nhiên phải do từng tác vụ định nghĩa.** Controller chung không tự quyết một đoạn là heading và không suy ngữ nghĩa từ từ khóa. Layout/OXML tạo quan sát về block, hàng, cell, paragraph, trang, explicit break; chúng là tín hiệu về vị trí cắt, không là semantic authority.

| Tác vụ | Đơn vị ưu tiên | Cắt an toàn và evidence phải giữ | Rủi ro khi dùng splitter chung |
|---|---|---|---|
| PDF selection | Trang, rồi parser block/visual row | Mỗi alias có đúng một owner target; lặp context theo manifest riêng | Cắt mất peer context; tách hai atom cùng visual row |
| Scan selection | Trang + paragraph/layout blocks | Dùng chosen reading/extent; crop identity, bbox, facts giữ nguyên | Ảnh có byte lớn nhưng token phụ thuộc xử lý vision; đổi resolution có thể mất chữ |
| DOCX selection | Paragraph/table row/OOXML container | Giữ paragraph intact nếu có thể; không bịa physical page vì chưa paginate | Chẻ paragraph làm mất run formatting/extent coordinate |
| Grouping | Một selected contiguous run | Chỉ chia nếu task chứng minh kết quả con ghép được hoặc có protocol xử lý boundary qua cut | Chẻ run khiến model không còn khả năng đề xuất group đi qua ranh giới |
| Hierarchy | Target edge batch + graph/candidate catalog | Chia targets nhưng giữ candidate ancestors và evidence task yêu cầu | Cắt luôn candidate universe làm parent đúng không còn hiện diện |
| OCR/transcription | Trang hoặc vùng ảnh trong reading order | Phải có transformation/crop mapping và quy tắc hợp nhất overlap | Duplicate text, mất dòng qua crop edge, sai tọa độ |
| Tool result | Evidence slice có paging cursor | Giữ facts liên quan + source identity; phân biệt trả về và đã gửi ở POST sau | Tool success nhưng hết budget trước POST vẫn bị ghi thành evidence đã dùng |

Một trang có thể chứa nhiều section; một section/table/list có thể kéo dài qua trang. Vì vậy policy nên là `PreferWholePage` cho PDF selection, không phải `PageIsSemanticBoundary`. Tác vụ cần cross-page context vẫn được cung cấp evidence lân cận. DOCX không nên lấy số trang từ guess để dùng cùng policy.

Không nên dùng ordinary bin packing: nó thường cho phép đổi thứ tự hoặc gộp các phần không liền nhau, trái source order và scope contract. Với ordered ranges, đây là bài toán constrained ordered partitioning:

1. Fixed-count: dễ replay nhưng bỏ qua mật độ facts, ảnh, output cardinality và ranh giới layout.
2. Greedy next-fit theo thứ tự: thử thêm đơn vị tự nhiên kế tiếp, serialize + đo; đơn giản và deterministic. Cần ưu tiên thử cả trang trước khi hạ xuống block để không lặp lỗi page 15.
3. Dynamic programming trên legal cuts: tối ưu cost của chuỗi segment theo `dp[j] = min(dp[i] + cost(i,j))` cho mọi request `(i,j)` hợp lệ. Cost có thể gồm số POST dự kiến, evidence lặp, penalty cắt trang/block và độ dài vượt soft quality target. Có ích nếu greedy tạo nhiều tail nhỏ hoặc ranh giới xấu; không tự giải semantic correctness.
4. Semantic chunker dựa model/embedding: có thêm chi phí và variance; không cần cho phiên bản đầu. Một model chia segment cũng không được đổi membership/authority.

Đề xuất bản đầu: ordered greedy + hierarchy legal splits + bounded lookahead, snapshot mọi quyết định. Chỉ thêm DP khi có metric chứng minh nhu cầu. Không tận dụng toàn bộ context window chỉ vì còn chỗ: soft target có thể nhỏ hơn hard limit sau qualification.

**Không binary-search kích thước packet khi chưa chứng minh monotone.** Khi tăng target range, một alias có thể chuyển từ context sang target, context đầu/cuối thay đổi, facts bị dedup, style examples/candidate catalogs thay đổi; bytes thực có thể giảm ở một bước rồi tăng lại. Tokenizer còn có merge xuyên biên chuỗi, nên tổng token từng atom không phải token request nối lại. Bản đầu nên enumerate legal natural-unit ends trong một bounded neighborhood, build/measure/cache theo scope hash, chọn scope hợp lệ có penalty tốt nhất. Binary search chỉ dùng ở adapter có bằng chứng/kiểm tra invariant monotonic cho đúng packet builder; một approximate additive size model chỉ dùng prune nhanh, final admission vẫn đo request thật.

### Gaps

- Các nguồn Docling/Unstructured giải bài toán representation và RAG; chưa chứng minh extraction cùng peer consistency của 072 sẽ ổn trên corpus PDF khác. Page 15 chỉ là bằng chứng cục bộ cho request scope.
- Chưa có measured quality curve theo model/tác vụ/độ dài trong repository. Không được lấy con số 32K từ benchmark cũ làm cap chất lượng cho Qwen hiện tại.
- Parser layout có thể sai reading order hoặc block membership. Packing không chữa được source corruption; cần ghi source/layout version và giữ khả năng replay.

## 2. Thiết kế C# dùng chung nên có contract và thuật toán như thế nào?

### Takeaway

Một `AdaptiveRequestController` dùng chung có thể quyết định tài nguyên, còn task adapter giữ quyền về đơn vị, evidence bắt buộc, composition và parser. Byte hard cap, token context, completion/reasoning cap, quality target và run spend là các chiều độc lập; admission phải kiểm tra tất cả.

### Cited Findings

- API token counting của Claude nhận input theo cấu trúc gồm system/tools/images/PDF; một số dạng input không hỗ trợ counting dù Messages API hỗ trợ. Tài liệu cũng gọi count là estimate. Controller vì thế cần provenance và mức tin cậy của count, không một hàm `bytes / 4` được coi là exact. — [Token counting](https://platform.claude.com/docs/en/build-with-claude/token-counting).
- OpenAI hướng dẫn đặt completion cap gần output dự kiến; max_tokens quá lớn có thể ảnh hưởng admission rate limit. Đây là quy tắc provider cụ thể, không được áp nguyên công thức sang provider khác. — [Rate limits](https://developers.openai.com/api/docs/guides/rate-limits).
- Repository hiện dùng `request.Bytes` để so `PromptTokenBudget`, chọn price tier và reserve input cost. Đây là conservative surrogate chứ không phải số token thực; `RecordActual` cộng actual nhưng không giải phóng cumulative reserve. Phải bảo toàn ý nghĩa cũ khi migration, không đổi tên rồi coi như đã có ledger động. — [AgentBudget](../../src/DocxHeaderExtractor.Agents/Budget/AgentBudget.cs).

### Inferences

**Các contract gợi ý, chưa phải code đã triển khai:**

```csharp
record ModelResourceProfile(
    string ProfileId, string ModelId, string? ProviderId,
    int ContextWindowTokens, int MaxCompletionTokens,
    long? ProviderRequestByteLimit, long OperatorRequestByteLimit,
    bool CompletionCapIncludesReasoning,
    string TokenizerRevision, string PricingRevision,
    DateTimeOffset VerifiedAt);

record RunEnvelope(
    decimal MaxUsd, long MaxInputTokens, long MaxOutputTokens,
    int MaxPosts, DateTimeOffset Deadline,
    int MaxInFlight, ResourcePolicyVersion Policy);

record NaturalUnit(
    string UnitKey, SourceRange Range, UnitKind Kind,
    IReadOnlyList<NaturalUnit> Children,
    bool MaySplit, bool MayMergeWithNext, SplitCost CutPenalty);

record EvidenceRequirement(
    string EvidenceKey, SourceRange Range, bool Required,
    EvidencePriority Priority, string SnapshotSha256);

record OutputDemand(
    int ResponseItemCount, int MinimumVisibleTokens,
    int PredictedVisibleTokens, int TailVisibleTokens,
    int ReasoningReserveTokens, string EstimatorRevision);

record ResourceEstimate(
    long Utf8Bytes, TokenRange InputTokens,
    int RequestedCompletionTokens, int EnforcedCompletionTokens,
    decimal UpperCostUsd, string CountMethod,
    string RequestSha256, string ProfileId);

interface IAdaptiveDocumentTask {
    string TaskContractVersion { get; }
    IReadOnlyList<NaturalUnit> NaturalUnits(SourceSnapshot source);
    EvidenceScope RequiredEvidence(TargetScope targets);
    OutputDemand EstimateOutput(TargetScope targets, CalibrationSnapshot stats);
    PreparedRequest Prepare(TargetScope targets, EvidenceScope evidence,
                            ConversationState state, int completionCap);
    SplitResult SplitAtLegalBoundary(TargetScope scope, FailureClass cause);
    bool CanCompose(IReadOnlyList<TargetScope> childScopes);
    TaskValidation Validate(ModelTurn turn, TargetScope targets);
}

interface IResourceMeter {
    ValueTask<TokenRange> CountAsync(PreparedRequest request,
                                   ModelResourceProfile profile);
}

interface IRunBudgetLedger {
    ValueTask<ReservationLease> ReserveAsync(AdmissionRequest request);
    ValueTask ReconcileAsync(ReservationLease lease, ProviderUsage usage);
}
```

Record thiết kế nên dùng unit types hoặc tên rõ để không so bytes với tokens. `TokenRange` chứa estimate, conservative upper bound, counter/tokenizer revision và confidence; local tokenizer chỉ exact với chuỗi mà nó thực sự tokenize, không tự bao gồm hidden chat template/server tool overhead. Provider count cũng không tự đảm bảo chi phí bill cuối. `Prepare` phải trả đúng body sẽ gửi; transport không được thay model/max_tokens/history sau khi đo mà không đo lại. Có thể cho transport finalize trước rồi trả immutable prepared body.

**Tính ngân sách mỗi POST:**

```text
B_wire <= min(provider body limit nếu biết, operator body ceiling)
T_input_upper + O_completion_enforced + M_count <= C_model
T_input_upper <= C_quality_target nếu task đã qualify target này
O_completion_enforced >= O_minimum_required
spent + outstandingReservations + thisReservation <= run ceiling
```

`C_quality_target` là soft target: task có thể cho phép một natural unit nguyên vẹn vượt target trong hard envelope, với observation `SOFT_TARGET_EXCEEDED_FOR_UNIT_INTEGRITY`. Nếu nó là quality ceiling bắt buộc đã qualify thì không vượt. Không đánh đồng hai loại.

Output reserve nên là hàm task, không một số chung 32.768:

- Selection: base JSON envelope + số target × token/decision (gồm alias, selection, reason được schema cho phép, extent/reading refs); dùng cardinality tối đa nếu chưa biết tỷ lệ SELECT. Không dự đoán "chỉ ít heading" rồi thiếu chỗ để trả NOT_SELECT cho mọi target.
- Grouping: ordered alias lists + số group tiềm năng + envelope/reason; số group tối đa có thể bằng số alias. Cần đủ response cap bytes riêng với output token cap.
- Hierarchy: số target × parent decision schema/evidence/reason; candidate catalog làm tăng input, không nhất thiết số output rows.
- OCR/transcription: output phụ thuộc diện tích nội dung/mật độ chữ/ngôn ngữ và schema tọa độ; cold start phải reserve bảo thủ hoặc chọn vùng nhỏ. Không có công thức count atom vì atom còn chưa được tạo.
- Tool turn: model có thể cần reasoning + tool arguments chứ chưa output final; completion cap có thể theo phase, nhưng task phải hỗ trợ việc đổi phase cap minh bạch.

Schema-bound size cần tính cả escaped aliases/evidence refs, arrays, field names và string escaping. Có thể tạo một syntactic maximum response fixture khi schema có finite max string/item counts rồi đo token/byte của fixture để lấy planning estimate; không gọi đó là proof token maximum trừ khi đã chứng minh tokenizer bound trên mọi chuỗi được phép. Alias/string variable length, Unicode và unknown analysis/reasoning khiến `n × 100 tokens` chỉ là heuristic. Schema không bound array/string cardinality thì chưa có finite schema upper bound; phải bổ sung contract bound hợp lý hoặc chấp nhận estimate có risk, không bịa exact upper limit.

Nếu provider gộp reasoning vào completion cap thì cộng visible + reasoning **một lần**; không trừ reasoning thêm lần nữa khỏi context. Nếu provider tính riêng phải adapter mô tả. Enforced cap thấp hơn minimum output demand phải làm request không admissible hoặc chia hợp lệ; không clamp im lặng rồi gửi request biết chắc thiếu output.

**Thuật toán lập kế hoạch:**

1. Freeze source snapshot, alias universe, task/instruction/schema versions, model profile và calibration snapshot.
2. Xây cây đơn vị tự nhiên và legal cuts. Ví dụ PDF: page → block → visual row → source atom; DOCX: container → paragraph → declared fragments với mapping nếu task hỗ trợ. Những cấp không có evidence chắc chắn không được bịa ra.
3. Thử page/run nguyên vẹn cùng required evidence và output demand. Serialize toàn request incl system, JSON escaped payload, tools, styles, history, model options; đo bytes thật; count/estimate tokens đúng profile.
4. Nếu vượt, trước hết phân loại chiều đang thiếu. Giảm optional evidence theo priority do task khai báo, ghi rõ scope mất đi; không cắt required evidence. Có thể page nguyên vẹn vừa byte nhưng không vừa completion vì quá nhiều output rows, khi đó chia targets nếu task composable.
5. Nếu cần cắt: chọn legal boundary penalty thấp, serialize lại các children. Không cắt trong một atomic unit chỉ để vượt qua gate. Nếu một atomic unit vẫn không vừa thì báo `UNSPLITTABLE_REQUIRED_SCOPE` hoặc route khác đã authorize/qualify; không auto bind singleton để "cứu".
6. Merge phần nhỏ liên tiếp nếu task cho phép và request hợp nhất vẫn admissible; không reorder source để tiết kiệm số POST.
7. Validate toàn plan: every target alias owned exactly once; contexts read-only có thể lặp; ownership union = universe; ranges source-ordered; selected-text/extent mapping unchanged.
8. Ghi immutable plan rồi execute. Mọi thay đổi runtime tạo revision với parent plan và cause; không rewrite lịch sử.

**Plan identity và replay:** `PlanId = hash(canonical source/evidence/universe IDs, task/instruction/schema versions, legal-cut policy, profile, calibration revision, ordered target scopes, evidence manifests, output policy)`. `RequestSha256` là hash bytes đúng từng POST. Token counting timestamp/network scheduling không nên tự đổi PlanId; profile và measured values được snapshot riêng. Nếu dùng learning online, freeze nó cho một document run hoặc ghi rõ stats revision mỗi revision để cùng document không bị đổi cách chia chỉ do thứ tự thread hoàn tất.

`UnitId` phải dựa source range + plan revision, không chỉ index SEG015. Child replans giữ `ParentUnitId`, split cause, attempt/continuation IDs. Hash safe audit không chứa plaintext; alias scope và resource fields có thể durable-export, raw body cần điều kiện lưu riêng.

### Gaps

- Cần thống nhất `ITask`/`ToolConversation` hiện tại có đủ boundary hooks cho planner hay cần một `PrepareNextTurn` hook trước admission. Chưa sửa code trong nghiên cứu này.
- Chưa xác minh provider cụ thể có reliable preflight count hoặc tokenizer phù hợp; provider/model có thể route khác theo thời gian. Không dùng profile vắng nguồn như hard guarantee.
- Upper monetary reserve tuyệt đối còn phụ thuộc pricing contract, cache billing, image/tool fees và usage missing; cần người quản trị đặt authorized hard ceiling và chính sách khi không biết.

## 3. Tự điều chỉnh thế nào để không vượt tiền, mất evidence hoặc tự đổi correctness?

### Takeaway

Tự điều chỉnh khả thi cho packing, output allocation, retries và concurrency trong envelope rõ ràng. Hệ thống không thể tự biết quality tối ưu hay giá trị ngân sách người dùng sẵn lòng trả; không được lấy protocol pass hoặc model confidence làm bằng chứng semantic đúng.

### Cited Findings

- Tài liệu context engineering của Anthropic mô tả việc curate toàn trạng thái mỗi lượt và ưu tiên context đủ thông tin nhưng gọn. Đây là engineering guidance, không phải guarantee rằng compaction hoặc tool retrieval luôn bảo toàn extraction semantics. — [Effective context engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents).
- OpenAI rate-limit response có thể chứa remaining/reset headers và Retry-After; failed retries cũng có thể tiêu rate capacity. Sizing completion quá cao còn ảnh hưởng rate estimate của provider này. — [OpenAI rate limits](https://developers.openai.com/api/docs/guides/rate-limits).
- .NET cung cấp concurrency limiter và token bucket/window limiters với semantics khác nhau; concurrency limiter giới hạn số request cùng lúc, không thay RPM/TPM limiter. — [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-9.0).
- Nghiên cứu scheduling output length năm 2026 đặt vấn đề dùng phân phối bất định thay point estimate vì output length là biến ngẫu nhiên. Kết quả của hệ serving trong paper không chuyển trực tiếp thành throughput improvement của HTTP client trong ứng dụng này. — [Zheng et al., ICML 2026](https://arxiv.org/abs/2604.00499).

### Inferences

**Ledger phải transaction trước dispatch.** Dùng `decimal` cho tiền, `long` cho token/byte counters. Tách `SpentConfirmed`, `SpentProvisionalUnknown`, `OutstandingReservations` và `Remaining`. Check + reserve atomically trên cả request count, spend và task/run allowance. Hủy trước gửi có thể hoàn trả reserve; hủy sau request đã gửi giữ provisional charge cho đến usage/billing. Reconcile phải idempotent theo attempt ID, không refund hai lần; late result vẫn được account tài nguyên nhưng không được writeback khi lease/cancellation đã thu hồi. Preserve cancellation/timeout và late-result quarantine hiện có.

Khi provider usage/cost thiếu, giữ upper reserve như provisional spend; đừng hoàn trả về 0 rồi phát thêm request. Khi actual vượt reserve, ghi drift, dừng admission mới hoặc dùng emergency envelope đã được authorize; không thể retroactively hoàn tác tiền đã tiêu. Dự phòng tiền cho retries/tool continuations nên là sub-budget rõ trong run ledger, không phân bổ toàn run cho first POST rồi làm mọi tool result bị bỏ giữa chừng.

Cần hai loại bảo đảm riêng: `max_posts`/`max_usd` hard; còn reserve cho một unit "có khả năng hoàn tất" là planned allowance. Không thể hứa mọi unit hoàn tất dưới finite budget nếu tool response hoặc model behavior không bị chặn. Mỗi tool cần declared maximum result bytes/tokens hoặc paging/truncation contract; nếu vượt phải trả lỗi/cursor theo contract, không JSON-truncate tùy tiện.

**Recheck ở mọi continuation:** Sau assistant tool_calls và tool result, build POST kế tiếp, serialize/count lại trước gửi. Không dùng kích thước first request cho mọi round. Nếu không vừa:

1. Loại duplicate evidence chỉ khi adapter chứng minh có nguồn đầy đủ khác trong context; giữ required facts, tool call/result protocol pairs và source identities.
2. Tool paging hoặc request smaller evidence scope theo tool contract; ghi returned vs observed vs actually consumed aliases.
3. Nếu task cho phép một fresh conversation trên scope con, tạo replan từ immutable source + complete required evidence; không mang partial JSON completion làm decision. Mọi child phải được parse/coverage hợp lệ trước composition.
4. Nếu không có thao tác bảo toàn contract thì dừng scope với failure code cụ thể. Không tự summarize document evidence bằng model rồi coi summary là thay thế authority tương đương.

**Bảng phản hồi runtime:**

| Tín hiệu | Hành động tài nguyên | Không được suy ra |
|---|---|---|
| First body vượt byte cap operator | Có thể tăng tới hard envelope cho phép; nếu không chia hợp lệ | Provider chắc chắn chấp nhận size lớn hơn |
| HTTP 413/provider payload limit | Hạ known provider byte ceiling; replan unit hợp lệ; scoped adaptation | Mọi model/provider dùng cùng limit |
| Context length error | Refresh profile; đo lại; giảm optional context/chia target | Chỉ cần giảm UTF8 bytes theo một hệ số tùy ý |
| finish=length | Không authorize response; tăng completion cap một lần hoặc chia hợp lệ trong budget | Parse được prefix nghĩa là output đủ/đúng |
| stop + malformed/incomplete coverage | Bounded protocol repair có feedback cụ thể theo contract | Tăng cycle vô hạn hoặc fill missing membership bằng C# |
| stop + valid result nhưng sai semantic | Giữ audit; đánh giá offline/corpus; không retry vì không thích kết quả | Resource controller có quyền phủ quyết model semantic |
| 429/503 transient | Tôn trọng Retry-After, bounded exponential backoff+jitter; giảm in-flight | Chia scope chữa được account quota exhaustion |
| Repeated timeouts/latency high | Giảm concurrency; tune size theo measured service behavior | Timeout chắc do input lớn hay có thể miễn phí retry |
| Tool succeeded nhưng next POST không admit | Ghi returned evidence, consumed=false | Tool evidence đã ảnh hưởng final verdict |

**Learning có kiểm soát:**

- Calibrate theo `(taskVersion, model, provider, tokenizer, modality, schemaVersion, reasoningMode)`, thêm cardinality/density features. Không trộn OCR image với text selection hoặc grouping.
- Theo dõi phân phối input count error, actual output/item, reasoning/output ratio, tool history growth, latency/cost. Có thể dùng quantile bảo thủ hoặc EWMA với min/max bounds và sample count; quantile từ 2 call không đáng tin. Cold start dùng defaults versioned của task, không xưng là auto-optimal.
- Output min schema demand là floor; learned target là estimate; hard cap là envelope. Khi có length events, cập nhật censored sample: output thực >= cap chứ không phải chính xác bằng cap.
- Mức tăng/giảm nhỏ, có hysteresis, cần đủ mẫu; không đổi model hoặc bỏ required evidence tự động. Profile/tokenizer/schema thay thì reset hoặc migrate calibration với provenance.
- Không dùng phản hồi model "tôi tự tin" để tăng batch hay chọn parent. Quality chỉ điều chỉnh từ đánh giá độc lập/source adjudication/Gold được phép, không semantic verifier authority trong runtime.
- Concurrency là một điều khiển riêng: RPM, TPM, provider concurrency, run reserve, deadline và fairness đều cần permit. Có thể giảm multiplicatively khi overload/429 rồi tăng chậm sau cửa sổ ổn định; đây là heuristic vận hành cần qualify, không bảo đảm theoretical optimum. Fair queue/aging tránh OCR scope lớn bị starvation bởi vô số selection scope nhỏ.

**Rollout đề xuất:**

**Migration nhỏ nhất phù hợp repository:** không cần rewrite `ToolConversation` hoặc thay semantic task ngay. (a) Bổ sung resource metadata vào `PreparedRequest`/companion record và cho `AgentBudget.Admit` dùng profile + typed estimate, vẫn giữ body/hash đúng bytes. (b) Tách helper dựng first request cho arbitrary `TargetScope` khỏi `SelectionTask` constructor để PDF planner đo payload đúng cùng builder; `IAgentSource.Scope` có thể đọc immutable plan thay vì fixed Cores, giữ adapter tương thích cho các lane còn lại. (c) Thay riêng PDF `CoresOf` bằng planner ưu tiên page, output-aware và legal splits; không đổi instruction/parser/binding. (d) Thêm hook `Prepare/MeasureNextTurn` ngay sau `task.Build(history, toolChoice)` và trước admission; loop hiện đã rebuild mỗi POST nên chỉ cần resource decision mở rộng chứ không tạo conversation loop mới. (e) Truyền completion options qua `AgentProtocol.Build` tới `OpenRouterWire.Build` và ledger đọc đúng enforced cap của body thay vì static `OpenRouterWire.MaxTokens`; option cũ vẫn default cho task chưa migrate. (f) Ledger reconcile dựa per-attempt usage được transport trả/callback, chưa dựa vào `ModelTurn` vì record hiện không có usage. Giữ request audit/hash, retry cùng bytes, parser fail-closed và authority hiện tại. Những thay đổi này không tự promote mọi task sang cùng policy.

Grouping cross-cut phải có quyết định thiết kế rõ trước migration: một run là atomic mặc định; nếu muốn split thì cần task contract dành cho boundary overlap/composition với ownership được định nghĩa chính xác, hoặc thêm grouping pass trên joint boundary và thay nhóm giao cut một cách coverage-valid. Không được xem "mọi child response valid" là chứng minh toàn run grouping đúng. OCR overlap cũng cần source-region coordinates và text-position matching/canonical source identity; đơn giản dedup theo text sẽ xóa các occurrence lặp thật trong tài liệu.

1. Chỉ telemetry và offline planner: resource profile, measured complete-body bytes, token estimate provenance, per-phase output demand, plan/request hash; chưa thay semantic contract. Replay toàn corpus để tìm request hiện đang bị chẻ chỉ vì 48 atom và phát hiện atomic unit không vừa.
2. Shadow packing PDF: chạy planner mới offline trên cùng snapshots, so exact coverage/source order/context manifests. Paired qualification giữ model/instruction/source cố định; thay duy nhất packing, predeclare document cohort/order và lặp để đo variance.
3. Bật page-preserving PDF selection theo policy version, byte/token/output checks đầy đủ và rollback flag. 072 page15 là regression cục bộ, không tiêu chí duy nhất để promote toàn corpus.
4. Sau đó thêm adapter DOCX/scan selection, rồi hierarchy/grouping/OCR; mỗi task có evaluation riêng về composition. Chưa cần migrate tất cả một lần.
5. Mở adaptive calibration sau khi ledger/counting/continuation guard ổn và đủ observations; snapshot calibration để replay được.

**Metric bắt buộc:** ownership coverage đúng 100%, alias duplicate/missing bằng 0; protocol completeness, truncation và retries; bytes/tokens first và max continuation; output cap requested/enforced; used/requested ratio; cost actual/provisional/reserved; absolute USD/run và tokens/useful heading; deadline/latency p50/p95 và rate errors; page/block split counts; context duplication; selection precision/recall/peer inconsistency, extent correctness, grouping false merge/split và hierarchy wrong-edge rate. `Placed`/`unresolved` đơn độc không đo semantic accuracy. Cần nguồn adjudication của metric được ghi rõ; local source review không được đổi tên thành Strict Gold.

### Gaps

- Không có kỹ thuật nào bảo đảm zero-config quality, arbitrary model/provider compatibility, luôn giữ nguyên trang và finite spend đồng thời. Unbounded single unit/tool history là phản ví dụ rõ; cần một hard envelope do người vận hành đặt và failure state minh bạch.
- Chưa có bằng chứng để chọn con số soft target, quantile, growth factor, concurrency window hoặc emergency reserve phù hợp corpus này. Phải benchmark/qualify; không thay 48 atom bằng một magic number 128K mới rồi gọi là adaptive.
- Học tài nguyên từ usage có thể giúp cost/latency nhưng không tự phát hiện các false-negative hợp lệ về JSON. Muốn kết luận chất lượng tốt hơn cần source-grounded evaluation nhiều tài liệu, giữ ranh giới authority đã rollback.

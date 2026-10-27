# Ràng buộc provider/model cho phân đoạn và ngân sách tự điều chỉnh

## 1. Có thể đọc những giới hạn nào trước khi tạo packet?

### Takeaway

Phải dùng metadata của **endpoint thực được phép route tới**, không dùng duy nhất context window quảng cáo của model. Không có căn cứ nguồn chính thức để coi 128 KiB/256 KiB là trần HTTP của OpenRouter; byte cap ứng dụng và token cap model là hai đại lượng độc lập.

### Cited Findings

- `GET /api/v1/models` cung cấp `context_length`, `architecture.tokenizer`, modalities, `supported_parameters`, `pricing`, `top_provider.max_completion_tokens`; `per_request_limits` có thể null. Đây là catalog model, không đủ thay thế catalog từng provider. — [Models API](https://openrouter.ai/docs/api/api-reference/models/get-models)
- `GET /api/v1/models/{author}/{slug}/endpoints` expose từng endpoint, gồm context length, max prompt/completion tokens, pricing, supported parameters và latency/throughput metadata. — [Endpoint API schema](https://openrouter.ai/docs/api/api-reference/endpoints/list-all-endpoints-for-a-model)
- Snapshot public API `qwen/qwen3.7-flash` lúc **2026-10-23T17:02:25.9273228Z**, tức **2026-10-24 00:02:25 +07:00**: tokenizer `Qwen`; hai endpoint Alibaba `alibaba` và `alibaba/us-east-1`; context **1,000,000**, max prompt **983,616**, max completion **65,536**. Hỗ trợ `max_tokens`, reasoning, response_format, tools/tool_choice; tool-choice auto/none có hỗ trợ, required/function không có. Base endpoint giá USD/M token input/output **0.03/0.13**; từ 32,000 prompt token **0.10/0.40**; từ 256,000 **0.20/0.80**. US endpoint có giá khác. — [Metadata trực tiếp](https://openrouter.ai/api/v1/models/qwen/qwen3.7-flash/endpoints)
- Bản gốc JSON và timestamp/hash lưu cạnh note: `openrouter-qwen37-endpoints.json`, `openrouter-qwen37-endpoints.fetch.json`. SHA256 JSON: `FEDE47DFB28A532E8486275C51052B8D43508A1122153CB1C683DA88FC6C849B`. Đây là snapshot tại thời điểm fetch, không phải lời hứa cấu hình provider sẽ cố định.
- Alibaba trực tiếp công bố Qwen3.7 Flash context 1,000,000; max input thường 991,808/thinking 983,616, max output 131,072. **Output 131,072 mâu thuẫn với endpoint OpenRouter 65,536**; giới hạn direct Alibaba không được nhập làm khả năng route OpenRouter. — [Alibaba model info](https://www.alibabacloud.com/help/en/model-studio/qwen3-7-flash)
- `max_tokens`/`max_completion_tokens` giới hạn generation; giá trị tối đa còn phụ thuộc context trừ prompt. Các parameter bị bỏ khỏi request không được OpenRouter tự điền: provider dùng default riêng. — [Parameters](https://openrouter.ai/docs/api_reference/parameters)
- `provider.allow_fallbacks` mặc định true, `require_parameters` mặc định false. Khi false, provider có thể bỏ qua parameter không hỗ trợ. `only` giới hạn provider; base slug có thể match nhiều region/variant. — [Provider routing](https://openrouter.ai/docs/guides/routing/provider-selection)

### Inferences

- Thiết kế profile nên có provenance `fetchedAt`, model ID, endpoint tags, source URL/hash và TTL. Metadata thiếu/null phải là **unknown**, không phải 0 hoặc unlimited. Dữ liệu catalog cần cache để không biến mọi POST thành một vòng fetch metadata.
- Preflight bảo thủ nên yêu cầu đồng thời:

  ```text
  inputUpper <= endpoint.max_prompt_tokens
  outputCap <= endpoint.max_completion_tokens
  inputUpper + outputCap + uncertaintyMargin <= endpoint.context_length
  serializedBodyBytes <= applicationTransportCeiling
  ```

  Đây là **policy bảo thủ đề xuất**, không phải mô tả chính xác token reservation nội bộ Qwen. `max_prompt_tokens` và `max_completion_tokens` không cộng độc lập thành context lớn hơn.
- Nếu cho nhiều endpoint fallback, dùng giới hạn nhỏ nhất trong tập endpoint hợp lệ; hoặc chọn endpoint đủ capability trước rồi tính packet theo nó. Không được tính với endpoint lớn rồi route tự do sang endpoint nhỏ hơn.
- Budget mềm có thể tăng theo nhiệm vụ nhưng trần cứng provider, user money/deadline và application memory phải tách riêng. “Adaptive” không có nghĩa tự ý nâng mọi trần khi gặp failure.
- Giá có bậc theo độ dài input: gộp packet lớn giảm overhead nhưng có thể đẩy **cả input/output của call** sang bậc đắt hơn; tối ưu số call đơn thuần có thể tăng tiền. Cần planner đánh giá theo pricing tier của endpoint và tính lại cho từng tool turn.

### Gaps

- Không tìm được nguồn chính thức công bố một **HTTP JSON request-body cap chung** cho OpenRouter chat completions. Không được suy luận “không tìm thấy cap” thành “unlimited”; upstream, proxy hoặc media upload có thể có cap riêng.
- Không có bằng chứng metadata `supported_parameters=[reasoning]` đảm bảo mọi subfield reasoning/effort/thinking-budget đều được Qwen3.7 Flash route này thực thi.
- Một trường tokenizer family `Qwen` chưa chỉ ra chính xác tokenizer revision, chat template, provider-added system tokens và ảnh preprocessing. Chưa thể gọi local token counter là exact chỉ từ trường này.
- Context đầy 1M là khả năng nhận request, chưa là chứng minh model giữ chất lượng extraction tốt ở 1M; cần qualification riêng.

## 2. Token, tool history, ảnh và reasoning cần được tính thế nào?

### Takeaway

Body bytes có thể đo chính xác trước POST; native token/cost cuối cùng do provider báo sau generation. Trước POST phải cộng toàn conversation, tool schema/results, ảnh và output reserve, rồi ghi rõ phần nào exact/phần nào ước lượng.

### Cited Findings

- OpenRouter response usage dùng **native tokenizer**; `prompt_tokens` bao gồm ảnh, audio input và tools. Có `reasoning_tokens`, cache details và cost; normalized finish reasons có stop, length, tool_calls, content_filter, error; giữ được raw `native_finish_reason`. Generation ID cho phép tra stats sau đó. — [Response/usage schema](https://openrouter.ai/docs/api_reference/overview)
- Usage accounting hiện ghi usage được trả tự động; với streaming nằm ở SSE cuối. `usage.include` và `stream_options.include_usage` đã deprecated/không còn tác dụng. Một số bài support cũ vẫn bảo thêm cờ; ưu tiên tài liệu accounting hiện tại. — [Usage Accounting](https://openrouter.ai/docs/cookbook/administration/usage-accounting)
- Trên **đa số provider**, reasoning và visible output cùng ăn `max_tokens`. Reasoning có thể dùng hết budget và trả `length` với content rỗng; `reasoning.exclude=true` chỉ giấu reasoning, không xóa phí/token budget. Hỗ trợ `reasoning.max_tokens` trên Alibaba thay đổi theo model. — [Reasoning tokens](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens)
- Qwen chat API stateless: client gửi lại conversation history mỗi turn. Token tính trên chat formatting, không chỉ content thô: ví dụ chính thức một “Hi” token thành 11 token khi thêm markers/assistant prefix ở model minh họa. Không được lấy con số 11 làm constant cho Qwen3.7. — [Alibaba text generation](https://www.alibabacloud.com/help/en/model-studio/text-generation)
- Client tools flow đưa assistant tool call và tool result trở lại messages trước call tiếp; tool schema tiếp tục là thành phần request. — [OpenRouter client tools](https://openrouter.ai/docs/guides/features/tool-calling)
- Với ảnh/video, tổng image+text tokens vẫn phải trong max input; số ảnh tối đa phụ thuộc phương thức input/model. Pixel resizing/detail cũng ảnh hưởng mức xử lý; số byte base64 không phải native image token count. — [Alibaba vision](https://docs.modelstudio.console.alibabacloud.com/en/model-studio/vision); [Multimodal chat parameters](https://www.alibabacloud.com/help/en/model-studio/qwen-api-via-openai-chat-completions)
- Tokenization phụ thuộc model; Alibaba yêu cầu đối chiếu usage API thực thay vì tự coi số ký tự/từ là token. — [Model usage](https://www.alibabacloud.com/help/en/model-studio/model-usage-statistics)

### Inferences

- Phân loại observability đề xuất:

  | Trước POST biết chính xác | Trước POST chỉ estimate | Sau response mới biết |
  |---|---|---|
  | UTF-8 bytes của body thực; hash; aliases; schema; số ảnh; pixel dimensions; output cap đã enforce | native input tokens nếu tokenizer/template không khớp; image tokens; reasoning/visible-output demand; latency; cost chưa settlement | native prompt/completion/reasoning/cache tokens; actual route/model; cost nếu provider báo; finish reason; actual response bytes |

- Planner cần lấy **body đã serialize từ transport thật**, không nhân “số atom × chi phí bình quân” làm truth. Local tokenizer nếu có phải token hóa đúng message/template/tools, kiểm tra chênh lệch với reported usage; fallback estimate ghi uncertainty/method/version.
- Không có cơ sở đặt một tỷ lệ bytes/token phổ quát cho tiếng Việt, OCR, JSON aliases, code và base64. Bytes vẫn có ích cho memory/network cap, nhưng không thay token context admission.
- Mỗi task cần output model riêng: selection trả status/extent mỗi atom; grouping trả partition; parent proposal trả một edge mỗi heading; OCR trả text dài. Quy mô input bằng nhau không tạo nhu cầu output giống nhau.
- Sau tool result, rebuild và đo lại request. Dùng projected headroom cho planning nhưng admission mỗi POST vẫn bắt buộc; lịch sử có thể lớn hơn dự báo. Khi không đủ ngân sách, không âm thầm cắt target/evidence hoặc xóa tool history cần cho protocol.
- Nếu `length`, completion chưa hoàn chỉnh phải fail-closed. Retry chỉ có giá trị khi điều chỉnh nguyên nhân có cấu trúc: tăng output trong hard cap hoặc giảm scope; không salvage prefix parse được.

### Gaps

- Trong tài liệu OpenRouter đã duyệt và tìm kiếm chính thức, **chưa xác nhận một token-count preflight endpoint tổng quát cho chat/tools/images của Qwen3.7 Flash**. Không khẳng định API đó không tồn tại; implementation phải capability-discover thay vì dựng URL hoặc gọi inference để “đếm miễn phí”.
- Alibaba OpenSearch có API tính token riêng nhưng đó là **sản phẩm Search**, không có bằng chứng tương đương chat-completions route này; không dùng thay counter của model.
- Không có reported usage thì chưa thể chứng minh chi phí 0. Stream timeout/missing final SSE phải ghi accounting unknown/pending, có thể reconcile bằng generation stats.
- Reasoning shared-budget là mô tả “đa số provider”, chưa có paid call nào trong nghiên cứu này để kiểm chứng riêng endpoint Qwen Alibaba. Không thay điều kiện đó thành khẳng định tuyệt đối.

## 3. Rate/concurrency/cost/latency và fallback phải phối hợp ra sao?

### Takeaway

Ngân sách cần ít nhất hai tầng: **per-call admission** và **run-wide scheduler/accounting**. Tăng cap request không tạo thêm TPM/RPM hay tiền; cancellation cũng không luôn dừng hóa đơn.

### Cited Findings

- OpenRouter tách credit limits, admin budget limits và rate limits. Có in-flight spending reservation dựa input và output được `max_tokens` cho phép; balance dương vẫn có thể không admit nhiều request cùng lúc. Ước tính reservation đó không bao gồm mọi request/plugin/image fees. — [Limits](https://openrouter.ai/docs/api_reference/limits)
- Paid models không có plan rate limit riêng theo pricing FAQ; rate limit thường từ provider, burst DDoS protection vẫn có thể 429. Free tier có giới hạn riêng. — [Pricing FAQ](https://openrouter.ai/pricing)
- Alibaba direct công bố qwen3.7-flash Singapore RPM 15,000, TPM 5,000,000; TPM gồm input+output và có thể enforce RPS/TPS. Đây là quota direct model/account/region, **không chứng minh quota hiệu lực của OpenRouter account đang chạy**. — [Alibaba rate limits](https://www.alibabacloud.com/help/tr/model-studio/rate-limit)
- Model fallback thử model khác khi context validation, moderation, rate limit/downtime; tính tiền theo model cuối thực dùng, trả trong response model. — [Model fallback](https://openrouter.ai/docs/guides/routing/model-fallbacks)
- Context compression plugin chủ động loại/cắt messages ở giữa để vừa context; được mô tả phù hợp khi không cần perfect recall. — [Message transforms](https://openrouter.ai/docs/guides/features/message-transforms)
- SSE terminal finish có thể xuất hiện lại trong usage frame, không phải hai completion. Mid-stream error có thể đến sau HTTP 200. **Alibaba nằm trong danh sách không hỗ trợ stream cancellation upstream**: abort kết nối không đảm bảo dừng model/billing; provider không hỗ trợ có thể tiếp tục chạy và bill toàn response. — [Streaming](https://openrouter.ai/docs/api_reference/streaming)
- Provider fallback thêm latency; recent median không phải deadline guarantee. — [Latency/performance](https://openrouter.ai/docs/features/latency-and-performance)

### Inferences

- Phân biệt:

  | Budget | Đơn vị | Ý nghĩa điều khiển |
  |---|---|---|
  | Body ceiling | UTF-8 bytes/call | Memory, proxy, serialization/network protection |
  | Context/input/output | native tokens/call | Model/provider admission và đủ chỗ cho output |
  | Rate | RPM/TPM, có thể RPS/TPS | Scheduler/token bucket theo provider; không quyết định semantic partition |
  | Concurrency | số in-flight | Local resources và in-flight cost; không suy ra trực tiếp từ RPM |
  | Run | USD, tokens, attempts, wall time | Tổng tiền/thời gian được user cho phép qua mọi task/tool/repair |

- Scheduler đề xuất reserve tiền/token cho POST trước khi chạy; sau response settle actualusage, release phần thừa. Reserve riêng work còn lại để selection không tiêu hết budget của grouping/placement. Metadata giá thiếu không nên tự tính 0.
- Với Alibaba abort, giữ reservation trạng thái unsettled và deadline quarantine kết quả; đừng lập tức hoàn tiền budget rồi mở nhiều retry gây double spend. Có thể poll generation stats theo bounded policy; khi không reconcile được giữ conservative upper reservation.
- 429 nên giảm concurrency/backoff có jitter theo scope quota; không tự cắt trang vì rate failure. Context overflow/HTTP size mới là tín hiệu replan scope; `length` là output demand; protocol coverage failure là vấn đề khác. Error-class-specific adaptation tránh chính một lỗi mạng làm mất context semantic.
- Exact extraction cần disable explicit compression/transforms và kiểm tra request thực không vô tình bật plugin qua preset. Fallback chỉ trong model/provider set đã qualify, log actual route; không tự đổi semantic model chỉ vì budget thấp.
- Không biến `max_tokens` thành output forecast. Đây là cap khả dụng; quá cao có thể tăng in-flight reservation và giảm concurrency, dù generation thực ngắn. Dùng telemetry theo task để ước lượng output kèm safety margin; vẫn phải đủ cho coverage bắt buộc.

### Gaps

- Chưa đọc secrets/account key nên chưa biết OpenRouter balance, per-key/org cap, effective rate quota hoặc actual DDoS thresholds. Không cần biết chúng để thiết kế interface; cần lấy runtime qua endpoint/headers hợp lệ và tránh export credential.
- Không có latency hard guarantee, công thức TPS/concurrency chung, hay headroom phần trăm “đúng cho mọi task” trong nguồn chính thức. P90/P95 observations có thể hướng dẫn soft budget; hard deadline vẫn do user/app.
- Nghiên cứu chỉ GET metadata/documentation; không có paid inference calls, không đo lại correctness, không sửa production hay chạy tests. Thời điểm client đã sang 2026-10-24; không gọi metadata snapshot này là dữ liệu lịch sử cố định của 2026-10-23.

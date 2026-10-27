# Kiểm tra selection PDF072: code hay LLM?

Lỗi trực tiếp ở các quyết định phân loại do LLM trả về. Chưa thấy code gán sai alias, làm mất target,
hay đổi SELECT thành NOT_SELECT trong những trường hợp đã kiểm tra. Kết luận này áp dụng cho hai
lượt thử source-bound gần nhất, không phải khẳng định toàn bộ extraction không có bug.

## Bằng chứng

Đã tái dựng hai request SEG-000 và SEG-012, đối chiếu hash với request thực tế trong log của cả hai
lượt. SHA-256 khớp chính xác, bao gồm prompt, nội dung, model, provider và các thiết lập wire.
SEG-012 có hash `3f177728321d5949860baf06050dd2408cc2f9828d8b1491b52bf3d189c5bd17`.
Model được đặt temperature=0; log vẫn có hai completion khác nhau. Hash giống nhau không cho biết
nguyên nhân nằm ở tính không tất định của inference hay thay đổi nội bộ phía provider.

60 target của hai scope có đúng text, page và facts chính so với evidence chuẩn đã ghi lại từ parser.
Các tham chiếu repeatedText cũng trỏ đúng source. Một số followingLine là preview bị rút ngắn theo
giới hạn của tool; đây không phải text của target bị cắt. Ba alias bị bỏ sót đều nằm trong targets,
không nằm ngoài scope hoặc chỉ xuất hiện trong context.

| Alias | Nội dung | Gold | JSON LLM lượt trước | JSON LLM lượt mới | Sau code lượt mới |
|---|---|---|---|---|---|
| L0513:S0 | International Comparison Program (ICP) | SELECT | SELECT | NOT_SELECT | NOT_SELECT |
| L0514:S0 | TECHNICAL ADVISORY GROUP (TAG) | SELECT | SELECT | NOT_SELECT | NOT_SELECT |
| L0519:S0 | Agenda | SELECT | SELECT | NOT_SELECT | NOT_SELECT |
| L0001:S0 | TECHNICAL ADVISORY GROUP | NOT_SELECT | SELECT | SELECT | SELECT |
| L0002:S0 | MARCH 7-8, 2025 | NOT_SELECT | SELECT | SELECT | SELECT |
| L0003:S0 | New York - Hybrid | NOT_SELECT | SELECT | SELECT | SELECT |

L0000:S0 được kiểm tra làm đối chứng: Gold và cả hai completion đều SELECT, code giữ nguyên.
Mỗi scope có đúng một phản hồi, finish_reason=stop, không gọi tool hay repair. Quyết định trong
Content gốc khớp quyết định unit đã được nhận, InitialSelection và FinalSelection ở cả hai lượt.
Không dùng trường Reasoning để suy ra quyết định.

Trong phần analysis công khai của JSON, lượt mới mô tả title và tên nhóm trên trang agenda là
cover metadata nên loại; lượt trước coi chúng là title và label của một đơn vị nội dung nên chọn.
Ở đầu tài liệu, LLM lại chọn ngày và địa điểm vì coi chúng là phần chia thông tin trên bìa. Đây là
lời giải thích do model tự báo cáo, chỉ có giá trị chẩn đoán; chính các decision trong JSON mới
là bằng chứng trực tiếp về nơi phát sinh lỗi.

## Phần code chịu trách nhiệm gì?

- `SelectionTask.Judge` nhận `ParseSelect(...).Values` để tạo outcome, không tự phân loại lại.
- `AgentProtocol.ParseSelect` kiểm tra schema, alias, coverage, evidence và sourceText; nó lưu
  chính giá trị decision do model gửi. NOT_SELECT không bắt buộc có evidence hoặc reason.
- `AgentSelector` đưa các SELECT đã nhận vào grouping. Các alias L0513/L0514/L0519 đã là
  NOT_SELECT trước grouping. Metadata đầu tài liệu đã được LLM SELECT trước khi grouping gộp
  chúng với L0000; grouping không tự thêm các alias này.
- Parent placement chạy sau selection. Lượt mới không phát sinh call chain-repair, nên repair
  không thể là bước đã làm mất ba alias.

Thiết kế hiện tại vẫn có giới hạn: sourceText ngăn gán sai nội dung cho alias nhưng không xác minh
đúng/sai ngữ nghĩa heading. Prompt yêu cầu đọc chức năng tại vị trí thực tế, song chưa nêu rõ
trường hợp title mở đầu tài liệu con/phụ lục so với metadata đi kèm. Nhận định về khả năng cần làm
rõ prompt là hướng thử nghiệm, chưa phải nguyên nhân đã được chứng minh bằng A/B.

Hướng sửa phù hợp là kiểm chứng tiêu chí/prompt và ngữ cảnh cho các trường hợp này, sau đó cân nhắc
review ngữ nghĩa có giới hạn. Không có bằng chứng để sửa parser nhằm tự đổi nhãn hoặc phục hồi các
alias theo Gold. Không thay đổi code runtime hay gọi provider mới trong lần audit này.

Script tái hiện: `artifacts/qualification/pdf072-parent-chain-repair-20261024/selection-cause-audit/audit.py`.
Kết quả: `audit.json` cùng thư mục, kèm hai request tái dựng khớp hash. Gold DOC-0252 được đối chiếu
hash với registry hiện tại và giữ nguyên. Audit này không tái kiểm tra ảnh gốc PDF.

## Đối chiếu phương pháp cũ và file ngày 23/10

Phương pháp cũ cũng để LLM quyết định SELECT/NOT_SELECT theo instruction về chức năng của từng
occurrence. Code kiểm tra alias, coverage, schema và evidence/extent; phản hồi hợp lệ được nhận,
không có bước xác minh đúng/sai ngữ nghĩa từng quyết định. SELECT cần evidenceRefs; NOT_SELECT
có thể không kèm evidence hoặc lý do. Grouping chia các alias đã chọn, placement xử lý parent sau đó.

File `pdf072-full-page15-20261023/all-heading-results-with-placement.json` có 42 heading nhưng
không phải 42 nhóm đều đúng: selection chọn 52 alias, gồm đủ 44 alias Gold và 8 alias thừa.
Grouping khớp chính xác 39/42 nhóm Gold. Bản cũ chọn cả L0513 đến L0519 rồi gộp title, thông tin
họp, ngày, địa điểm, địa chỉ và Agenda vào một heading; title đầu tài liệu cũng gộp metadata.
Manifest ghi rõ đây là lượt qualification (`productionAuthorization=false`).

Request SEG-012 ngày 23/10 giống từng byte với request baseline retry-fixed đã đối chiếu,
SHA-256 `cc63768b6f2d68594b96c9600b54c0140824c4d115211dbe3cabdcca3fe5925e`.
So với candidate source-bound, toàn bộ user payload, instruction, targets và facts vẫn bằng nhau;
system prompt/schema thay đổi để yêu cầu sourceText cho SELECT. Đây là thay đổi có thể ảnh hưởng
completion, nên không thể khẳng định prompt mới hoàn toàn không liên quan. Tuy nhiên, hai lượt
source-bound có cùng request đầy đủ vẫn cho quyết định khác nhau; bằng chứng hiện có không tách
được tác động của thay đổi protocol khỏi biến động của model/provider.

Do đó không có cơ sở coi phương pháp cũ là kết quả đúng ổn định. Các lượt thể hiện lỗi chọn thừa,
chọn thiếu và grouping khác nhau. `old-method-comparison.json` trong thư mục audit lưu phép đối chiếu
và các số đo theo Gold hiện tại; không dùng tổng số heading để thay thế chấm alias và boundary.

## Kiểm chứng batch song song và bản sửa tiếp theo

Audit tiếp theo xác nhận cả hai lượt source-bound đều có 650 alias được sở hữu đúng một lần,
15 scope hoàn tất, không có khác biệt giữa decision nhận từ provider và decision code thu về.
Test collector thực tế với parallelism 1/6/6 và thứ tự hoàn tất bị đảo vẫn bảo toàn mọi decision
và hash request. Trong thử nghiệm provider 16 call, ba alias bị thiếu còn tái hiện khi chạy nối tiếp.
Vì vậy không có bằng chứng lỗi thu kết quả do batch song song trong các trường hợp đã kiểm tra.
Chạy nối tiếp cũng không tự bổ sung ngữ cảnh từ scope trước vào scope sau. Kết luận này không
loại trừ ảnh hưởng chưa biết của tải provider đến kết quả suy luận.

V2/V3/V4 không đạt gate và được giữ nguyên làm bằng chứng thất bại. V5 làm rõ tên phần nội dung
so với thuộc tính ngày/địa điểm/hình thức họp, đồng thời thử review tối đa hai scope. Bốn phép thử
review có đầu vào selection cố định đều đạt: PDF072 hai lần phục hồi 44/44 alias Gold, giảm thừa
3 → 1; scope đầu PDF076 hai lần phục hồi 5/5 alias Gold, không chọn thừa. Đây chưa phải số đo toàn
pipeline. Chi tiết, kết quả chạy đầy đủ và quyết định promotion được ghi riêng tại
[báo cáo V5](pdf072-membership-v5-review-20261024.md). Không đổi nhãn bằng keyword hay sửa Gold.

Hai lượt V5 chạy đầy đủ đều chọn đủ 44 alias Gold, còn một alias thừa. Tuy nhiên grouping cho
42 heading (41/42 nhóm Gold) ở lượt đầu và 35 heading (28/42 nhóm Gold) ở lượt hai, dù request
grouping giống từng byte. Vì vậy cần phân biệt **bỏ sót selection** với **gộp sai boundary**.
Lượt đầu còn có một review hết bốn attempt HTTP 429. Cả hai đều dưới 300 giây nhưng không đạt
gate tổng thể; không promote. Mỗi heading và mọi thay đổi alias được xuất trong
`pdf072-membership-v5-review-20261024/full-01/heading-review.md` và `full-02/heading-review.md`
dưới `artifacts/qualification/`.

Bản thử tách instruction grouping riêng giữ nguyên request selection và đạt 247 test khi chạy
hồi quy không kèm build. Trên selection cố định, hai lượt chỉ đạt 39/42 và 41/42 nhóm Gold; lượt đầu
vẫn gộp DAY 2 với SESSION V. Bản này cũng không promote. Xem
[báo cáo boundary](pdf072-membership-boundaries-20261024.md) để đọc cả kết quả thất bại và giới hạn
kiểm chứng. Chưa có bằng chứng rằng chỉ sửa prompt đã đảm bảo độ chính xác production.

## Kết quả sửa tiếp và promotion ngày 25/10

Profile sau cùng đổi contract grouping thành JOIN/SPLIT cho từng cặp dòng, kiểm tra đúng alias và
selectedText, bổ sung layout/style facts và bảo toàn title nhiều dòng. Membership V6 phân biệt nhãn
tổ chức mở đầu nhóm người tham gia với từng người/affiliation trong entry. Không phục hồi verifier
làm trọng tài và không dùng C# tách theo từ khóa. Toàn bộ các thử nghiệm thất bại phía trên được giữ.

Hai lượt PDF072 đạt 236,614 / 248,588 giây, đủ 44/44 alias Gold, còn một alias metadata thừa và
41/42 nhóm Gold trong cả hai lượt. PDF076 chạy đầy đủ đạt 191,239 giây, đủ 35/35 alias và 34/34 nhóm,
khôi phục cả tám nhãn tổ chức từng bị NOT_SELECT. Hồi quy cuối đạt 300/300 test. Default native PDF
trong code đã được promote ngày 2026-10-25; không có thao tác deploy server bên ngoài. Xem
[báo cáo cuối và giới hạn còn lại](pdf072-membership-v6-pairwise-20261024.md) cùng
`artifacts/qualification/pdf072-membership-v6-pairwise-20261024/qualification-result.json`.

# Kiến trúc trích outline PDF born-digital

## Những quyết định nào có thể lấy trực tiếp từ cấu trúc PDF mà không suy diễn ngữ nghĩa?

### Takeaway
Một đường “native-structure first” có thể loại bỏ hoàn toàn LLM cho một phần tài liệu: dùng cây bookmark và/hoặc cây tag PDF làm **bằng chứng tác giả đã mã hoá**, rồi chỉ xác minh việc neo vào nội dung/trang. Nhưng font, toạ độ và mẫu đánh số chỉ là tín hiệu hình thức để tạo ứng viên hoặc kiểm tra nhất quán; chúng không chứng minh một dòng là heading.

### Cited Findings
- PDF Document Outline (bookmark) là một cây phân cấp các outline item phục vụ điều hướng tương tác; đây là cấu trúc TOC gốc, có thể thiếu hoàn toàn vì đặc tả nói tài liệu *may contain* nó. [ISO 32000-1 excerpt](https://learn.microsoft.com/en-us/openspecs/ie_standards/ms-pdf/063cf79a-bdbd-40cc-a62f-27f1c08527c1)
- Tagged PDF quy định cấu trúc logic, thứ tự nội dung, và các standard structure types gồm heading, paragraph, table; nó cũng phân biệt “real content” với layout/pagination artifacts. [PDF 32000-1, §14.8](https://developer.adobe.com/document-services/docs/assets/35e4369068f86065372c18787171a17e/PDF_ISO_32000-1.pdf)
- `/H1`–`/H6` trong structure tree là markup heading cụ thể; W3C nêu rõ heading PDF có thể được đánh dấu bằng các phần tử `/Hn`. [W3C PDF9](https://www.w3.org/WAI/WCAG22/Techniques/pdf/PDF9.html)
- PDF standard cho phép block-level structure element bị gián đoạn (ví dụ qua ngắt trang); consumer có thể tái lắp lại qua logical structure. Vì vậy không được mặc định rằng một heading/native block chỉ nằm trên một page packet. [PDF Reference 1.6, structure elements](https://www.pdfill.com/download/PDFReference16.pdf)
- Tags có thể sai: hướng dẫn PDF/UA yêu cầu kiểm tra rằng nội dung thực sự là heading mới được tag heading, heading levels phù hợp và không bỏ cấp. Đây là yêu cầu chất lượng đối với file tốt, không phải bằng chứng rằng mọi tagged PDF trong thực tế đều đúng. [PDF Association PDF120](https://pdfa.org/techniques-for-accessible-pdf/headings-with-different-levels-correctly-tagged/UA1_Tpdf-H_03/)
- Outline cũng không tự bảo đảm ngữ nghĩa hay độ phủ: ISO định nghĩa cơ chế file format nhưng không đặt yêu cầu/hướng dẫn tác giả cho bookmark; hierarchy do tác giả/phần mềm tạo quyết định. [PDF Association on bookmarks](https://pdfa.org/pdf-ux-bookmarks/)
- Layout token của GROBID giữ text Unicode, font size/name, bold/italic/super/subscript và bounding boxes. Điều này chứng minh born-digital PDF cung cấp đủ tín hiệu native để làm feature extraction không cần OCR/ảnh cho nhiều bước hình thức. [GROBID principles](https://github.com/grobidOrg/grobid/blob/master/doc/Principles.md)

### Inferences
- Thiết kế route theo độ tin cậy: (A) nếu outline có destination nội bộ và title khớp một span native gần anchor, xuất outline như “author-provided”; (B) nếu tagged structure có mapped `/H`/`/Hn`, lấy heading/order/level từ tag tree; (C) chỉ khi hai nguồn thiếu, mâu thuẫn, hoặc không neo được mới chạy V14/V12/V2. Đường A/B thay thế call LLM, không chỉ nén prompt.
- Hệ thống phải lưu `evidence_type` và `evidence_conflict`, không hợp nhất outline/tag/font thành một nhãn “semantic certainty”. Ví dụ title trên trang bìa, running header, TOC entry, bold table cell và caption đều có thể giống heading về style.
- Deterministic candidate pruning nên chỉ loại/giảm ưu tiên dựa trên quan sát kiểm chứng được: text không hiển thị hoặc ngoài crop box, lặp cùng toạ độ ở nhiều trang (running header/footer), vùng table/figure, content marked Artifact, dòng quá ngắn/dài, continuation line; và giữ mọi span có native outline/tag anchor. Không nên loại chỉ vì “trông không giống heading” khi chưa đo recall.

### Gaps
- Không có nguồn công khai nào đo trực tiếp tỷ lệ document production của V14/V12/V2 có usable bookmarks hoặc tag tree đúng. Cần instrument trên tập traffic thật: presence, destination-resolve rate, text-anchor match rate, tag-vs-gold precision/recall theo publisher/template.
- Không có bằng chứng từ nguồn đã xem rằng font hierarchy có một ngưỡng tổng quát đủ an toàn để quyết định heading semantics. Mọi cutoff font/weight/numbering phải là policy có đo lường, không phải rule “đúng”.

## Global pass, cascade và select-then-local repair có đáng giá hơn page packets không?

### Takeaway
Có triển vọng lớn hơn tiết kiệm 1–5% serialization: PDF outline là bài toán hierarchy/document-level, còn page packet làm mất bằng chứng lặp xuyên trang, style vocabulary toàn tài liệu và thứ tự/cấp cha-con. Hướng khả thi nhất là một global native pass rẻ để lập candidate/evidence graph, sau đó chỉ gửi các component bất định sang local multimodal repair; chưa có chứng cứ rằng một global LLM/VLM gọi duy nhất sẽ thắng về chi phí hoặc độ chính xác.

### Cited Findings
- GROBID công khai dùng cascade: output model trước được đưa vào các model sau; tài liệu của dự án nói cascade cho phép chọn model/features/training phù hợp từng cấu trúc và giảm tác động class imbalance bằng cách tách nhãn hiếm khỏi majority class. [GROBID principles](https://github.com/grobidOrg/grobid/blob/master/doc/Principles.md)
- GROBID segmentation trước hết tách vùng cấp tài liệu như title page, header, body, headnotes/footnotes và bibliography, sau đó các model chuyên biệt xử lý các phần. Đây là tiền lệ production-oriented cho “zone first, specialise second”, thay vì yêu cầu một model/LLM nhìn mọi token. [GROBID principles](https://github.com/grobidOrg/grobid/blob/master/doc/Principles.md)
- Nghiên cứu layout cho thấy domain shift là thực: DocLayNet báo các model train trên nguồn hẹp như PubLayNet/DocBank giảm đáng kể khi gặp layout đa dạng, và benchmark models còn thấp khoảng 10% so với inter-annotator agreement. [DocLayNet paper](https://arxiv.org/abs/2206.01062)
- LayoutLM đồng mô hình hoá text và layout; bài báo nhấn mạnh font/position/layout bị NLP text-only bỏ qua và chúng hữu ích với document understanding. [LayoutLM paper](https://arxiv.org/abs/1912.13318)
- DocLLM báo một kiến trúc layout-aware chỉ dùng text và bounding boxes, tránh image encoder đắt tiền, đồng thời vượt các baseline trong 14/16 benchmark datasets mà tác giả đánh giá. Đây là bằng chứng nghiên cứu rằng “global/layout tokens trước, pixel khi cần” là hướng hợp lý, không phải bằng chứng trực tiếp cho outline extraction. [DocLLM paper](https://arxiv.org/abs/2401.00908)
- Tổng quan ICCV 2025 mô tả reading order, TOC extraction và hierarchical structure prediction là các bài toán cấu trúc phức tạp; các phương pháp khác nhau giữa suy luận quan hệ pairwise rồi dựng hierarchy và dựng hierarchy trực tiếp bằng chuỗi quyết định có cấu trúc. [ICCV survey](https://openaccess.thecvf.com/content/ICCV2025W/VisionDocs/html/Giovannini_A_Survey_on_Reading_Order_Table_of_Contents_and_Structure_ICCVW_2025_paper.html)

### Inferences
- Kiến trúc đề xuất để thử nghiệm (không phải khẳng định đã thắng):
  1. **Native global index, không LLM**: parse outline/tag tree; extract line/block/word geometry, font/style, drawing/table zones, repetition histogram, numbering grammar, TOC-like pages và reading-order edges. Gán ID ổn định và lưu evidence provenance.
  2. **Candidate graph, không gán nghĩa**: union của native-tag/bookmark anchors, numbered/style-change lines và layout-detector title candidates; nối cạnh “đọc tiếp”, “cùng style family”, “có thể là parent/child” với score hình thức. Global pass nhận ra running headers, templates, section-number consistency và cross-page continuations mà page packet đơn lẻ không thấy.
  3. **Deterministic accept/reject phần hiển nhiên**: accept author-provided evidence đạt neo/nhất quán; reject non-content/artifact/repeated-margin/table-cell candidates; phần còn lại tạo connected components/boundary windows quanh ambiguity thay vì toàn bộ trang.
  4. **Selective GROUP/PLACE repair**: LLM/VLM chỉ nhận component nhỏ gồm candidate lines + neighbour windows + global style/outline summary + crop ảnh cần thiết. GROUP gộp multiline/cross-page headings; PLACE chọn parent/level giữa các parent candidates, với constraint không tạo cycle và tôn trọng observed native anchors. Không yêu cầu model tái xuất toàn outline.
  5. **Global deterministic reconciliation**: sort theo reading order, enforce tree validity, preserve source text/coordinates, flag rather than silently “fix” conflict. Nếu cần review, gửi only conflict components.
- Select-then-local repair có lợi thế token lớn khi số candidate/ambiguity thấp hơn số page. Nhưng threshold phải tối ưu theo **gold heading recall**: pruning làm mất heading là lỗi không cứu được bằng verifier.
- Một document-level LLM call có thể cải thiện semantic context nhưng không được mặc định là breakthrough: token/image cost tăng theo số trang, context window có thể cắt tài liệu, và tác động domain shift vẫn cần chứng minh. Global *deterministic* evidence index là thay đổi ít rủi ro hơn để thử trước.

### Gaps
- Các nghiên cứu trên không benchmark chính xác pipeline V14/V12/V2 hay đo token/API cost trên born-digital PDFs của dự án. Không thể ngoại suy mức giảm chi phí hoặc tăng F1.
- Cần offline ablation với gold document outlines: page packets hiện tại; +native routing; +global candidate graph; +selective local repair; +global LLM. Báo heading P/R/F1, exact text/span match, level accuracy, parent-edge F1, tree validity, p50/p95 latency, pages/images/tokens và cost/document, tách theo template/domain/độ dài.

## Escalation và verifier nào cần chứng minh online trước khi đưa production?

### Takeaway
Verifier nên kiểm tra các mệnh đề có thể chứng minh từ evidence (anchor, text/span, order, tree constraints, tag/bookmark agreement), không yêu cầu LLM tự khẳng định “đúng”. Dùng abstention/escalation có calibration giữ cho expensive model tập trung vào uncertainty thật; tuy nhiên threshold và verifier score chỉ được tin sau calibration trên dữ liệu đại diện và online shadow experiment.

### Cited Findings
- SelectiveNet định nghĩa selective prediction/reject option, tối ưu prediction và rejection cùng nhau, và báo risk–coverage trade-off tốt hơn các baseline mà tác giả thử nghiệm. Nó là cơ sở cho thiết kế “chấp nhận khi đủ tin, còn lại abstain/escalate”, không phải bằng chứng sẵn có cho PDF. [ICML 2019 paper](https://proceedings.mlr.press/v97/geifman19a.html)
- Conformal Risk Control mở rộng conformal prediction để kiểm soát kỳ vọng của monotone loss và nêu ví dụ NLP/computer vision, gồm false-negative rate, graph distance và token-level F1; bảo đảm của nó phụ thuộc calibration/exchangeability assumptions. [ICLR 2024 paper](https://arxiv.org/abs/2208.02814)
- Structured-document error không chỉ là detection: tổng quan ICCV phân biệt entity detection và construction of hierarchical structure/relations. Điều này ủng hộ đo verifier ở cả node, edge và tree, thay vì chỉ “dòng có phải heading?”. [ICCV survey](https://openaccess.thecvf.com/content/ICCV2025W/VisionDocs/html/Giovannini_A_Survey_on_Reading_Order_Table_of_Contents_and_Structure_ICCVW_2025_paper.html)
- PDF/UA guidance cho heading nêu các điều kiện kiểm tra cụ thể: heading thực sự phải được tag, tag không dùng sai, first Hn là H1, cấp phù hợp hierarchy, và không skip level. Đây là ví dụ tốt về constraint verifier, nhưng chỉ áp dụng như quality rule khi profile/tags đáng tin. [PDF Association PDF120](https://pdfa.org/techniques-for-accessible-pdf/headings-with-different-levels-correctly-tagged/UA1_Tpdf-H_03/)

### Inferences
- Tách verifier thành hai lớp độc lập: **hard evidence verifier** (destination resolves; output text normalizes/matches span; bounding box visible; source order monotonic; no duplicate same anchor; parent precedes child; acyclic tree; level transition policy; tag/bookmark agreement/conflict) và **semantic/visual verifier** chỉ cho residual (crop + native text hỏi “đây có là section heading không, parent nào?”). Lớp hard có thể reject/route mà không sinh tokens.
- Không dùng “model tự chấm model” làm gate duy nhất. Một escalation score có thể ghép: thiếu/mâu thuẫn native evidence; candidate margin gần decision boundary; style family entropy toàn document; numbering discontinuity; graph alternative count; parser anomalies. Calibrate score theo *document families*, vì DocLayNet chứng minh layout generalisation suy giảm khi domain đổi.
- Online proof plan: shadow-run baseline và proposed route trên traffic, định tuyến/không làm thay output user; lấy stratified adjudicated sample có oversample route accept, route reject, conflict và long/multi-column docs. Trước khi bật: (1) non-inferiority recall cho heading và parent edge, (2) upper confidence bound cho catastrophic tree failures, (3) realized cost/latency, (4) calibration/reliability by template, (5) drift alarms cho native-evidence coverage. Rollout theo canary và automatic fallback baseline khi native parser/validator bất thường.
- Thước đo success phải là chi phí trên **correct outline**, không chỉ token/document: token saving đi kèm recall loss là regression. Đặc biệt đo escaped false negatives (heading bị deterministic prune) và false positive accepted without LLM.

### Gaps
- Chưa có calibration set/gold taxonomy của V14/V12/V2 nên không thể chọn threshold acceptance, target coverage hoặc risk budget. Cần một held-out, versioned labeled corpus trước khi dùng conformal/selective guarantee.
- Online A/B chỉ đáng tin khi có human/gold adjudication cho một mẫu ngẫu nhiên được phân tầng; feedback người dùng đơn thuần sẽ bỏ sót silent outline errors. Cần quyết định trước ngân sách adjudication, acceptance criteria và rollback owner.

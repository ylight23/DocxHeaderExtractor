---
name: heading-extraction
description: Phân tích cây heading DOCX theo source-grounded, precision-first workflow.
version: 1.0.0
---

# Heading extraction

## Mục tiêu

Xác định vai trò ngữ nghĩa và cấp heading nhưng không bịa, sửa hoặc làm mất nội dung nguồn.

## Nguồn chuẩn

- OOXML và canonical document model là source of truth.
- View gửi LLM chỉ là phép chiếu trung lập: content + JSON metadata; không dùng `#`/`##` để
  đánh dấu sẵn heading.
- Giữ `index`, `stableId`, thứ tự tài liệu, nguồn `paragraph`/`table_cell`, style, numbering,
  định dạng và source span.
- Model chỉ trả role, source index và level. Text kết quả phải lấy lại từ nguồn bằng code.

## Workflow

1. Parse paragraph, table cell và các nguồn văn bản được hỗ trợ theo đúng document order.
2. Dùng rule để lấy evidence và ứng viên; rule không được biến một cụm từ riêng lẻ thành đáp án.
3. Context builder thêm block cha, anh em và lân cận cần thiết.
4. Analyzer phân loại `heading`, `document_title`, `table_header`, `form_label`,
   `signature_label`, `caption`, `normal_text` hoặc `uncertain`.
5. Critic đánh giá độc lập các heading được đề xuất; không nhìn kết luận cũ để tránh anchoring.
6. Deterministic validator kiểm tra ID, level, thứ tự, source span và tính hợp lệ của cây.
7. Chỉ auto-accept theo evidence đã calibration trên holdout; còn mâu thuẫn thì human review.

## Điều kiện dừng

- Thành công khi output qua validator và không còn mục bắt buộc review.
- Dừng ở human review khi thiếu bằng chứng hoặc các lượt semantic bất đồng.
- Không tự ghi nhãn vàng, không tự tăng confidence, không tự sửa prompt/code và không retry vô hạn.

## Data boundary

- Có API key không đồng nghĩa với được gửi tài liệu ra ngoài.
- Backend remote chỉ chạy khi caller đồng ý cho đúng run đó.
- Trace không lưu nội dung tài liệu hoặc API key.

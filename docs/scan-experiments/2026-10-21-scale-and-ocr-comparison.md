# So sánh tiền xử lý và tỷ lệ phóng VLM trên 023 — 2026-10-21

Tài liệu: `todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx`, 34 trang scan.

## Quyết định

- **Tỷ lệ phóng production giữ ở 2** (`ScanTranscriptionPreflight.Options.Scale = 2.0`, không đổi).
  Lý do: scale 1 không cho thấy kém hơn rõ rệt trên Haiku, và trên Qwen có một dòng nhiều dòng bị bộ kiểm tra từ chối.
- **Nguồn dòng production là OpenCV** (`--segmented-lines` là mặc định). Tesseract chỉ dùng khi chạy với `--tesseract-lines`.
- Lệnh `dhx scan-ocr` đã bỏ. Nó chỉ tạo store Tesseract riêng và không còn cần cho `scan-extract`.

## 1. Tạo evidence: OpenCV so với Tesseract (023, Release, mỗi đường một lần)

| Đường | Thời gian | Số dòng |
|---|---|---|
| OpenCV (`ScanSegmentPreflight`) | 9,2 s | 1.115 |
| Tesseract (`ScanOcrPreflight`) | 244 s | 1.084 |

OpenCV nhanh khoảng 26 lần và tìm được nhiều dòng hơn.

## 2. Đọc ảnh và cắt crop (đo trước đó, cùng tài liệu)

- Đọc xám cho 34 trang (Release, đã khởi động JIT): `PngRaster.LoadGrey` 185 ms mỗi trang, đường managed `PngRaster.Load` 348 ms mỗi trang. Mức xám trùng hoàn toàn trên 34 trang.
- Cắt crop cho 1.115 dòng: `CropBilevel` mất khoảng 8–9 s ở build Debug, từ khoảng 17 s trước khi tối ưu mask. Trên Release, lần đo đầu là 3,17 s với raster đã nạp sẵn. Tất cả 1.115 PNG khớp từng byte với bản trước khi tối ưu.
- Ghi chú về bộ nhớ: đọc xám bằng OpenCV tốn khoảng 14,4 MB managed mỗi trang trước khi bỏ bản sao, còn 3,7 MB sau khi bỏ. Phần native của ảnh OpenCV khoảng 10,7 MB mỗi trang.

## 3. Phiên âm: tỷ lệ phóng 1 và 2

Gold là 112 dòng của s060. Chữ gold do Qwen đọc ở scale 2 trong lần chạy s060.

| Lần đọc | Khớp hoàn toàn | Khớp khi bỏ dấu | CER | Ghi chú |
|---|---|---|---|---|
| Haiku scale 1 | 110/112 | 110/112 | 0,90% | 12 lô, mù, không gọi provider |
| Haiku scale 2 | 110/112 | 110/112 | 2,71% | lần chạy replay trước đó, cùng prompt |
| Qwen scale 1 | 109/112 | 110/112 | 0,82% | 1 lần, 112 lần gọi, 0,0171 USD, 0 lỗi gọi API |

Ba dòng không khớp trên Qwen scale 1:
- **P002-L021:** gold chỉ có phần đầu tiêu đề, còn Qwen đọc cả dòng. Đây là lỗi so sánh, không phải lỗi đọc.
- **P023-L001:** Qwen trả về nhiều dòng trong một ảnh, và bộ kiểm tra `REJECTED_MULTILINE` loại dòng này. Đây là rủi ro thật của scale 1.
- **P006-L024:** "sổ" đọc thành "số" (lỗi dấu một ký tự).

Giới hạn:
- Không có lần scale 2 Qwen mới để so trực tiếp. Gold là kết quả s060 trước đó, nên so sánh Qwen scale 1 với gold không hoàn toàn là A/B.
- Haiku không phải mô hình production. Số Haiku chỉ cho biết xu hướng, không thay được Qwen.

## 4. Pipeline 023 chạy bằng Haiku (không gọi provider)

- 6 lượt, dừng khi không còn request nào chờ.
- Kết quả cuối: 85 heading trước khi sửa gold, 24/24 đơn vị hoàn thành, 1.112/1.115 dòng phiên âm được chấp nhận.
- Gold được sửa hai lần sau khi so với kết quả này, theo quyết định của chủ dự án: thêm P002-L022 và P002-L023 (dòng nối của Điều 3), và bỏ P030-L001 "Chú thích:". Gold hiện là 112 dòng.
- Sau khi sửa, kết quả khớp 112/112. Con số này cho biết gold đã khớp với pipeline, không phải độ chính xác, vì gold được sửa sau khi xem kết quả.

## Việc còn lại

1. Nếu muốn so scale 1 với scale 2 trên Qwen một cách công bằng, cần một lần scale 2 Qwen cùng cấu hình. Cần bạn cho phép.
2. Xử lý `REJECTED_MULTILINE` cho dòng P023-L001 trước khi xem xét scale 1.
3. Chưa commit.

# Cache nguồn PDF và DOCX giữa các lần chạy — thiết kế (chưa triển khai)

Ngày: 2026-10-22. Trạng thái: thiết kế và kiểm tra khả thi, chưa có code cache.

## Vì sao cần

Mỗi lần chạy, nguồn PDF được dựng lại từ đầu. Trên PDF 001 (5.447 dòng) việc này mất khoảng 9 giây khi đã khởi động, trong đó PdfPig đọc ký tự khoảng 2 giây, và phần còn lại là ngữ cảnh, phân tích annotation và gom khối bố cục. Với DOCX, việc đọc và dựng nguồn mất khoảng 1–2 giây. Cache chỉ có ý nghĩa khi cùng một file được chạy lại nhiều lần (thí nghiệm, kiểm thử, chạy lại sau lỗi).

## Khóa cache

- SHA-256 của file nguồn.
- Phiên bản parser (`PdfSourceFactsVersions`, phiên bản dựng nguồn DOCX).
- Phiên bản schema của cache (tăng khi đổi hình dạng dữ liệu được lưu).

Khóa gồm ba phần này, nên đổi bất kỳ phần nào là bỏ qua cache cũ.

## Kiểm tra khả thi (đã làm)

| Thành phần | Kết quả |
|---|---|
| `DocumentSourceSnapshot` (PDF 005, 2.850 nguyên tử) | Tuần tự hóa được, khoảng 10,5 MB. Đọc lại thất bại: `DocumentSourceCatalog` có tham số constructor không khớp thuộc tính. Cần DTO hoặc converter. |
| `PdfSourceDetails` | Tuần tự hóa được, nhưng khoảng 69 MB. Quá lớn vì mỗi khối lưu lại ngữ cảnh trước/sau. |

Kết luận: không cache `PdfSourceDetails` nguyên dạng. Cần một dạng nén chỉ giữ thông tin mà các bước sau thực sự đọc.

## Kiểm chứng trước khi dùng cache

1. Khi đọc cache, dựng lại `SourceAliasUniverseHash` và `ModelVisibleEvidenceHash` từ dữ liệu vừa nạp, so với giá trị lưu trong cache. Không khớp thì bỏ cache và dựng lại.
2. Không lưu kết quả lỗi (`EMPTY_UNIVERSE`, `SOURCE_CHANGED_DURING_PARSE`).
3. Ghi cache bằng file tạm rồi đổi tên, như `ScanSegmentPreflight` đang làm.

## Phạm vi đề xuất

- Giai đoạn 1: cache snapshot nhỏ (catalog, aliases, hash, atoms) cho DOCX và PDF.
- Giai đoạn 2: cache phần ngữ cảnh đã nén, nếu giai đoạn 1 đo được lợi ích.

## Việc chưa làm

- DTO cho `DocumentSourceCatalog`.
- Dạng nén cho `PdfSourceDetails`.
- Kiểm chứng hash trên toàn bộ tài liệu thí nghiệm.

# DOC-0205 semantic/span reconciliation examples

Offline-only examples from frozen artifacts; no Gold bytes or predictions were changed.

## C0 WrongSpan correspondences

- Gold 3: `Điều 3. Giải thích từ ngữ` → `SEMANTIC_PRESENT_PARTIAL_SPAN` prediction span `1629:1651`
- Gold 4: `Điều 4. Giá trị pháp lý của dữ liệu được chia sẻ` → `SEMANTIC_PRESENT_PARTIAL_SPAN` prediction span `4361:4399`
- Gold 5: `Điều 5. Nguyên tắc chung về quản lý, kết nối và chia sẻ dữ liệu` → `SEMANTIC_PRESENT_PARTIAL_SPAN` prediction span `4626:4672`
- Gold 6: `Điều 6. Thực hiện kết nối, chia sẻ dữ liệu` → `SEMANTIC_PRESENT_PARTIAL_SPAN` prediction span `6603:6633`
- Gold 7: `Điều 7. Yêu cầu trong việc quản lý, kết nối, chia sẻ dữ liệu` → `SEMANTIC_PRESENT_PARTIAL_SPAN` prediction span `8146:8188`

## C0 true omissions

- Gold 0: `Chương I QUY ĐỊNH CHUNG`
- Gold 1: `Điều 1. Phạm vi điều chỉnh`
- Gold 11: `Điều 9. Nguyên tắc quản lý dữ liệu, cơ sở dữ liệu trong cơ quan nhà nước`
- Gold 14: `Điều 12. Danh mục cơ sở dữ liệu quốc gia, duy trì danh mục cơ sở dữ liệu quốc gia`
- Gold 17: `Điều 14. Hoạt động quản trị dữ liệu, quản trị chia sẻ, khai thác dữ liệu`

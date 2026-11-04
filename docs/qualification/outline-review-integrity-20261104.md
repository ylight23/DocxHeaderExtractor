# Kiểm tra tính toàn vẹn duyệt outline — 04/11/2026

Các sửa đổi đang thử nghiệm:

- Quyết định duyệt phải khớp hash file, cấu hình, hash outline, người duyệt và thời điểm; quyết định heading phải khớp text, cấp, parent đã biết, span và source parts. Xác nhận `OUTLINE_COMPLETE` của một outline không dùng cho outline khác trên cùng file. Quyết định cũ thiếu hash outline không được coi là đã duyệt.
- Kho outline lưu riêng source parts khi có đủ dữ liệu lúc ghi và khôi phục khi đọc; schema JSON công khai của `HeadingRecord` không đổi. Entry cũ không có binding vẫn đọc được nhưng không được tự coi là đã duyệt. Chưa có chuyển đổi hay khôi phục dữ liệu thiếu cho cache cũ.
- So outline báo `BOUNDARY`, `BOUNDARY_UNRESOLVED`, `BINDING`, `BINDING_UNRESOLVED`, `UNBOUND_OLD/NEW`, `PARENT_UNRESOLVED` và `ALIGNMENT_UNRESOLVED`. Các suy đoán split/merge được đánh dấu chưa chắc. Parent không suy từ level null. `ContextChanged` chỉ nói về signature đoạn; `ContextEvidenceVerified` luôn false vì chưa kiểm tra style, numbering và tool.

Trang admin có tab "Duyệt outline" (khi bật "Dùng outline đã lưu"): hiện khác biệt với phiên bản lưu trước kèm đoạn trước/sau, nhận kết luận theo vị trí qua `POST /api/admin/outline/review`; server tự dựng quyết định từ outline đã lưu và từ chối khi hash outline không khớp. Để giảm số mục, thiếu binding ở một bên chỉ báo một mục `EVIDENCE_MISSING`, và lệch cấp chung cho đa số heading báo một mục `LEVEL_SHIFT`. Chưa có đo thời gian và số thao tác duyệt trên file thật. Một outline có binding và được xác nhận không tự chứng minh model đã tìm đủ heading: xác nhận đầy đủ là quyết định của người duyệt. Các heading không có source parts hoặc ranh giới chưa xác định không thể được coi là heading đã duyệt bởi `OutlineApprovals`.

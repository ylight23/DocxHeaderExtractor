# Holdout evaluation

Thư mục này chỉ chứa tài liệu thật chưa được dùng để chỉnh prompt, threshold, numbering hay code.
Mỗi `X.docx` phải đi cùng `X.key`, theo đúng format của `bench/README.md`.

## Tự động nhập tài liệu đã duyệt

Đặt các cặp sau vào một thư mục nguồn:

```text
bao-cao-01.docx
bao-cao-01.review.json
```

File review phải có `correctedLevel` cho mọi paragraph (`0` = non-heading, `1..9` = Heading).
Xem trước, không ghi gì:

```powershell
.\scripts\add-holdout.ps1 -SourceDirectory "D:\HoldoutMoi" -WhatIf
```

Nhập toàn bộ cặp hợp lệ:

```powershell
.\scripts\add-holdout.ps1 -SourceDirectory "D:\HoldoutMoi"
```

Nhập rồi chạy OpenRouter calibration ngay:

```powershell
.\scripts\add-holdout.ps1 -SourceDirectory "D:\HoldoutMoi" -RunCalibration
```

Script kiểm tra review hoàn tất, level/stable ID, SHA-256 chống trùng với development, không ghi
đè cặp đã tồn tại và lưu provenance vào `manifest.jsonl`. Nó không tự biến dự đoán AI thành nhãn vàng.

```powershell
dhx eval bench\holdout -m models\Qwen2.5-7B-Instruct-Q4_K_M.gguf --ctx 8192 --gpu-layers 99
```

Sinh precision calibration profile cho cổng auto-accept:

```powershell
.\dhx.cmd eval .\bench\holdout --openrouter --two-pass `
  --calibration-out .\bench\precision-calibration.json
$env:DHX_CALIBRATION_PROFILE = ".\bench\precision-calibration.json"
.\dhx-ui.cmd
```

Profile chia theo evidence signature, mặc định yêu cầu ít nhất 52 dự đoán mỗi bucket và dùng cận
dưới Wilson 95%. Một bucket đúng 5/5 nhưng chỉ có vài mẫu không được coi là đã đạt 93–95%.

Nguyên tắc:

- Không đổi code/prompt dựa trên kết quả holdout rồi báo lại chính kết quả đó là holdout.
- Khi cần sửa, chuyển file sang `bench/development`, làm thay đổi, rồi bổ sung holdout mới.
- Báo cáo luôn tách `development` và `holdout`; chỉ số holdout mới là số dùng để đánh giá tổng quát.

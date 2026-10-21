| Lần | Hybrid/Tesseract | Cắt unit | Lượt chọn | Unit | Heading | Lượt gọi | Chi phí | Hội thoại | Khoản/Điều 17 | F1 (Gold cũ) | Ghi chú |
|---|---|---|---|---|---|---|---|---|---|---|---|
| s046/hyb | hybrid | trang | 1 lượt | 27/33 | 80 (Điều 41) | 42 | $0.051 | 277 s | P020-L014 Y, P008-L024 Y | F1 0.596 (TP 65/FP 33/FN 55) | đúng code cũ, GROUP-002 lỗi |
| s046/prod | Tesseract | trang | 1 lượt | 30/30 | 87 (Điều 49) | 38 | $0.048 | 267 s | P020-L014 Y, P008-L024 Y | F1 0.953 (TP 112/FP 3/FN 8) | production cũ |
| s047/hyb2 | hybrid | trang | 1 lượt | 33/33 | 87 (Điều 49) | 39 | $0.049 | 225 s | P020-L014 Y, P008-L024 Y | F1 0.737 (TP 87/FP 29/FN 33) | quy tắc một mảng runs |
| s047/prod2 | Tesseract | trang | 1 lượt | 24/30 | 76 (Điều 48) | 40 | $0.045 | 325 s | P020-L014 N, P008-L024 Y | F1 0.898 (TP 101/FP 4/FN 19) | production, quy tắc một mảng runs; GROUP-003 lỗi |
| s048/c1 | hybrid | trang | 1 lượt | 27/33 | 78 (Điều 40) | 44 | $0.051 | 342 s | P020-L014 N, P008-L024 Y | F1 0.563 (TP 60/FP 33/FN 60) | ngữ cảnh 40 dòng; GROUP-001 lỗi ROOT_KEYS, 8 run rơi về dự phòng |
| s049 | hybrid | trang | 2 lượt (precedingSelected) | 33/33 | 89 (Điều 49) | 103 | $0.081 | 584 s | P020-L014 Y, P008-L024 Y | F1 0.715 (TP 84/FP 31/FN 36) | ngữ cảnh 40 dòng |
| s050 | hybrid | đoạn | 2 lượt (precedingSelected) | 24/24 | 89 (Điều 48) | 53 | $0.082 | 355 s | P020-L014 Y, P008-L024 N | F1 0.730 (TP 88/FP 33/FN 32) | không raw |
| s051 | hybrid | đoạn | 2 lượt (precedingSelected) | 24/24 | 86 (Điều 49) | 53 | $0.081 | 362 s | P020-L014 N, P008-L024 Y | F1 0.730 (TP 85/FP 28/FN 35) | raw |
| s052 | hybrid | đoạn | 2 lượt (precedingSelected) | 24/24 | 86 (Điều 49) | 54 | $0.083 | 374 s | P020-L014 N, P008-L024 Y | F1 0.723 (TP 85/FP 30/FN 35) | raw có req id |
| s053 | hybrid | đoạn | 1 lượt | 24/24 | 85 (Điều 49) | 29 | $0.046 | 210 s | P020-L014 Y, P008-L024 Y | F1 0.723 (TP 85/FP 30/FN 35) | transcript |
| s054 | hybrid | đoạn | 1 lượt | 24/24 | 85 (Điều 49) | 33 | $0.049 | 333 s | P020-L014 Y, P008-L024 Y | F1 0.720 (TP 85/FP 31/FN 35) | prompt "Read them before you decide" |
| s055 | hybrid | đoạn | 1 lượt | 8/24 | 25 (Điều 4) | 10 | $0.047 | 170 s | P020-L014 N, P008-L024 N | F1 0.215 (TP 16/FP 13/FN 104) | contextFacts trong request, trần 128 KB: 16/24 unit bị từ chối |
| s056 | hybrid | đoạn | 1 lượt | 24/24 | 88 (Điều 49) | 77 | $0.092 | 468 s | P020-L014 N, P008-L024 Y | F1 0.720 (TP 85/FP 31/FN 35) | gate get_line_facts bật: 51 lượt tool |
| s058 | hybrid | đoạn | 1 lượt | 24/24 | 84 (Điều 49) | 32 | $0.183 | 312 s | P020-L014 N, P008-L024 Y | F1 0.728 (TP 83/FP 25/FN 37) | contextFacts đúng, trần 256 KB |
| s059 | hybrid | đoạn | 1 lượt | 23/24 | 84 (Điều 49) | 79 | $0.081 | 340 s | P020-L014 Y, P008-L024 Y | F1 0.710 (TP 82/FP 29/FN 38) | gate get_scan_crop bật: 68 lượt crop |
| s060 | hybrid | đoạn | 1 lượt + kiểm tra nhất quán | 24/24 | 86 (Điều 49) | 29 | $0.048 | 263 s | P020-L014 N, P008-L024 Y | F1 0.719 (TP 83/FP 28/FN 37) | cấu hình được promote |

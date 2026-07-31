# Thư mục mô hình

Đặt file `.gguf` vào đây. Mặc định chương trình tìm file có tên chứa `llama-3.2-3b`,
nếu không có thì lấy file `.gguf` đầu tiên.

## Mô hình khuyến nghị

`Llama-3.2-3B-Instruct-Q4_K_M.gguf` — khoảng **2.0 GB**, cần ~3 GB RAM khi chạy với ngữ cảnh 4096.

Tải bằng script kèm theo:

```powershell
.\scripts\download-model.ps1
```

Hoặc tải thủ công từ Hugging Face (cần đăng nhập + chấp nhận điều khoản Llama 3.2 Community License):

- Kho chính thức: <https://huggingface.co/meta-llama/Llama-3.2-3B-Instruct>
- Bản GGUF cộng đồng: <https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF>
  → file `Llama-3.2-3B-Instruct-Q4_K_M.gguf`

## Chọn mức lượng tử hoá

| Mức      | Dung lượng | RAM (ctx 4096) | Ghi chú                                       |
|----------|-----------:|---------------:|-----------------------------------------------|
| Q4_K_M   |    ~2.0 GB |        ~3.0 GB | Cân bằng tốt nhất — mặc định của project        |
| Q5_K_M   |    ~2.3 GB |        ~3.3 GB | Chính xác hơn chút, chậm hơn ~20%              |
| Q8_0     |    ~3.4 GB |        ~4.4 GB | Gần như không mất chất lượng, chậm nhất        |
| Q3_K_M   |    ~1.7 GB |        ~2.6 GB | Chỉ dùng khi RAM rất hạn chế; hay sai cấp mục  |

Xem metadata của file đã tải:

```powershell
dhx info models\Llama-3.2-3B-Instruct-Q4_K_M.gguf
```

## Dùng mô hình khác

Bất kỳ mô hình instruct nào ở định dạng GGUF đều chạy được — LLamaSharp đọc chat template
nhúng trong file GGUF. Nếu file không có template, chương trình tự quay về template Llama 3.
Với mô hình quá nhỏ (< 1B) nên giảm `--chunk-tokens` xuống ~1200 để tránh bỏ sót.

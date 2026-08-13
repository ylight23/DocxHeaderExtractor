#!/usr/bin/env python3
"""Tìm xem một mục có THẬT SỰ tồn tại trong .docx hay không.

Trả lời đúng một câu hỏi: pipeline bỏ sót, hay tài liệu vốn không có mục đó.
Hai nguyên nhân này cần hai cách xử lý hoàn toàn khác nhau, và không phân biệt
được từ đầu ra của pipeline (handoff §46.5).

    python scripts/tim-muc-thieu.py <file.docx> V
"""
import re
import sys
import zipfile


def doan_van(path):
    xml = zipfile.ZipFile(path).read('word/document.xml').decode('utf8', 'ignore')
    for p in re.findall(r'<w:p[ >].*?</w:p>', xml, re.S):
        yield re.sub(r'\s+', ' ', ''.join(re.findall(r'<w:t[^>]*>(.*?)</w:t>', p, re.S))).strip()


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    path, ky_hieu = sys.argv[1], sys.argv[2]

    # Tìm cả ở ĐẦU đoạn lẫn GIỮA đoạn: bản chuyển PDF gộp cả trang vào một <w:p>,
    # nên mốc thường không nằm ở vị trí 0 (handoff §47.1: 1.596 ở đầu, 24.220 bên trong).
    dau = re.compile(r'^\s*' + re.escape(ky_hieu) + r'\s*[\.\):]')
    giua = re.compile(r'(?<![\w])' + re.escape(ky_hieu) + r'\s*[\.\):]\s*\S')

    thay = 0
    for i, t in enumerate(doan_van(path)):
        if not t:
            continue
        if dau.match(t):
            print(f'  ĐẦU ĐOẠN   i={i:<5} {t[:78]}')
            thay += 1
        elif giua.search(t):
            vt = giua.search(t).start()
            print(f'  GIỮA ĐOẠN  i={i:<5} …{t[max(0, vt - 30):vt + 60]}…')
            thay += 1

    print()
    if thay:
        print(f'=> Tài liệu CÓ {thay} chỗ khớp "{ky_hieu}". Pipeline bỏ sót, không phải tài liệu thiếu.')
        print('   Nếu chỗ khớp nằm GIỮA ĐOẠN: chạy lại kèm --split-merged.')
    else:
        print(f'=> Tài liệu KHÔNG có mục "{ky_hieu}" nào. Chính tác giả nhảy số.')
        print('   Cảnh báo của hậu kiểm là ĐÚNG và nên gửi lại cho người soạn.')
    return 0


if __name__ == '__main__':
    sys.exit(main())

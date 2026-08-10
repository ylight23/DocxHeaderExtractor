#!/usr/bin/env python3
"""
Tầng 1 hàng loạt — phân loại CHẾ ĐỘ tài liệu trên cả một thư mục .docx

Mục đích: biết tỷ trọng chế độ trong tập tài liệu thật, và quan trọng nhất
là biết BAO NHIÊU FILE KHÔNG RƠI VÀO CHẾ ĐỘ NÀO — con số đó cho biết spec
còn thiếu bao nhiêu chế độ.

Chạy trên máy bạn. Tài liệu KHÔNG rời khỏi máy.

Cài đặt:
    pip install lxml

Dùng:
    python tier1_batch.py /duong/dan/thu/muc
    python tier1_batch.py /duong/dan --csv thongke.csv --recursive

Đầu ra: CSV chỉ chứa SỐ LIỆU THỐNG KÊ, không chứa nội dung tài liệu.
Cột `sample_markers` chỉ ghi LOẠI ký hiệu (roman/dieu/alpha...), không ghi chữ.
An toàn để gửi đi phân tích.

Dùng chung canon.py cho việc đọc DOCX (an toàn, resolve style) — không tự
đọc/parse XML riêng, tránh hai bộ luật whitespace/run-merge lệch nhau.
"""

import argparse
import csv
import os
import re
import sys
import traceback
import unicodedata
import zipfile
from collections import Counter
from pathlib import Path

try:
    import canon
except ImportError:
    print("Thiếu canon.py (phải cùng thư mục).", file=sys.stderr)
    raise

VN_ALPHA = 'aăâbcdđeêghiklmnoôơpqrstuưvxy'

MARKERS = [
    ('phan',    re.compile(r'^\s*(?:PHẦN|Phần)\s+(?:thứ\s+)?([IVXLCM]+|\d+)\b')),
    ('chuong',  re.compile(r'^\s*(?:CHƯƠNG|Chương)\s+([IVXLCM]+|\d+)\b')),
    ('muc',     re.compile(r'^\s*(?:MỤC|Mục)\s+(\d+)\b')),
    ('dieu',    re.compile(r'^\s*(?:ĐIỀU|Điều)\s+(\d+)\s*[\.:]?\s')),
    ('roman',   re.compile(r'^\s*([IVXLCM]+)\s*[\.\)]\s*(?=\S)')),
    ('dec2',    re.compile(r'^\s*(\d{1,2}(?:\.\d{1,2})+)\s*\.?\s')),
    ('num',     re.compile(r'^\s*(\d{1,2})\s*[\.\)]\s*(?!\d)')),
    ('alpha',   re.compile(r'^\s*([' + VN_ALPHA + r'])\s*[\.\)]\s')),
    ('bullet',  re.compile(r'^\s*([-+*•])\s+(?=\S)')),
]

TYPED = re.compile(r'^\s*(\d+(?:\.\d+)+)')

BARE_HEAD = re.compile(
    r'^\s*(?:PHẦN|Phần|CHƯƠNG|Chương|MỤC|Mục|TIỂU MỤC|Tiểu mục)'
    r'\s+(?:thứ\s+)?[IVXLCM\d]+\s*[\.:]?\s*$')

HEADING_STYLES = re.compile(r'^(Heading[1-9]|Title|Tieu\s*de|Muc\s*cap|Chuong)', re.I)
BODY_LIKE_STYLES = {'Normal', 'BodyText', 'TableParagraph', 'ListParagraph', 'NormalWeb', 'Quote'}

# leader chấm/gạch nối trước số trang, và số trang trần cuối dòng TOC
_TOC_LEADER = re.compile(r'[\.․‧·\-\s]{2,}\d{1,4}\s*$')
_TOC_TRAILING_NUM = re.compile(r'\s+\d{1,4}\s*$')


def norm(s):
    return re.sub(r'\s+', ' ', unicodedata.normalize('NFC', s or '')).strip()


def normalize_toc_entry(s):
    s = _TOC_LEADER.sub('', s)
    s = _TOC_TRAILING_NUM.sub('', s)
    return norm(s)


def match_marker(t):
    for name, rx in MARKERS:
        if rx.match(t):
            return name
    return None


# ------------------------------------------------------------------ đếm cấu trúc XML
def _count_elem(raw, tag):
    """Đếm PHẦN TỬ thật <w:TAG ...>, không dính chuỗi con (vd. 'ins' không bắt 'insideH',
    'sdt' không bắt 'sdtPr'/'sdtContent', 'del' không bắt 'delText')."""
    return len(re.findall(r'<w:' + tag + r'(?=[ >/])', raw))


def raw_structure_counts(path):
    with zipfile.ZipFile(path) as zf:
        raw = zf.read('word/document.xml').decode('utf-8', 'ignore')
    return {
        'n_ins': _count_elem(raw, 'ins'), 'n_del': _count_elem(raw, 'del'),
        'n_sdt': _count_elem(raw, 'sdt'), 'n_field': _count_elem(raw, 'instrText'),
        'n_textbox': _count_elem(raw, 'txbxContent'), 'n_sect': _count_elem(raw, 'sectPr'),
        'n_tbl': _count_elem(raw, 'tbl'), 'n_drawing': _count_elem(raw, 'drawing'),
    }


# ---------------------------------------------------------------- phân loại
def classify(blocks, counts):
    out = {}
    # luồng chính: không bảng, không textbox (spec 3.2.4), không paragraph hỏng (spec X1)
    flat = [b for b in blocks if not b['in_table'] and not b['in_textbox'] and not b['corrupt']]
    hs = [b for b in blocks if b['style'] and HEADING_STYLES.match(b['style'])]
    toc = [b for b in blocks if b['style'] and b['style'].upper().startswith('TOC')
           and len(b['text']) > 6]

    out['n_block'] = len(blocks)
    out['n_flat'] = len(flat)
    out['n_table_block'] = sum(1 for b in blocks if b['in_table'])
    out['n_textbox_block'] = sum(1 for b in blocks if b['in_textbox'])
    out['n_corrupt'] = sum(1 for b in blocks if b['corrupt'])
    out['n_heading_style'] = len(hs)
    out['n_toc'] = len(toc)
    out['n_outlinelvl'] = sum(1 for b in blocks if b['outline'] is not None)
    out['n_numpr'] = sum(1 for b in blocks if b['numId'])
    out['n_center'] = sum(1 for b in flat if b['jc'] == 'center')
    out['n_bare_head'] = sum(1 for b in flat if BARE_HEAD.match(b['text']))

    mk = Counter()
    for b in flat:
        m = match_marker(b['text'])
        if m:
            mk[m] += 1
    out['sample_markers'] = '|'.join(f'{k}:{v}' for k, v in mk.most_common(6))

    # ---- baseline định dạng thân bài (đoạn dài > 200 ký tự) — spec 4.1
    longs = [b for b in flat if len(b['text']) > 200]
    baseline_bold = Counter(bool(b['bold']) for b in longs).most_common(1)[0][0] if longs else False
    sizes = [b['size_pt'] for b in longs if b['size_pt']]
    baseline_size = Counter(sizes).most_common(1)[0][0] if sizes else None

    def deviates(b):
        bold_diff = bool(b['bold']) != baseline_bold
        size_diff = baseline_size is not None and b['size_pt'] and abs(b['size_pt'] - baseline_size) >= 1
        return bold_diff or size_diff

    # ---- chỉ số quyết định (spec 4.1)
    r_outline_n = out['n_outlinelvl']
    r_numpr = (sum(1 for b in hs if b['ilvl'] and b['ilvl'].isdigit() and int(b['ilvl']) >= 1)
               / len(hs)) if hs else 0.0
    r_typed = (sum(1 for b in hs if TYPED.match(b['text'])) / len(hs)) if hs else 0.0
    vn_marks = sum(mk[k] for k in ('phan', 'chuong', 'muc', 'dieu', 'roman', 'alpha'))
    bold_flat = [b for b in flat if b['bold']]
    r_vnadmin = (vn_marks / len(bold_flat)) if bold_flat else 0.0
    r_legal = (mk['dieu'] + mk['chuong']) / max(len(flat), 1)

    # c_depth: với mỗi style dùng cho block "x.y..." gõ tay, block có cùng ĐỘ SÂU nhiều nhất
    # chiếm bao nhiêu % — đo mức style có phản ánh đúng cấp số hay không (spec 4.1)
    typed_blocks = [(b, TYPED.match(b['text'])) for b in flat]
    typed_blocks = [(b, m) for b, m in typed_blocks if m]
    by_style = {}
    for b, m in typed_blocks:
        depth = m.group(1).count('.')
        by_style.setdefault(b['style'] or '(none)', []).append(depth)
    if by_style:
        ratios = [Counter(d).most_common(1)[0][1] / len(d) for d in by_style.values() if d]
        c_depth = sum(ratios) / len(ratios) if ratios else 0.0
    else:
        c_depth = 0.0

    # TOC field khớp với body bao nhiêu % (spec 4.2: bắt buộc >= 80% mới tin TOC)
    toc_norm = {normalize_toc_entry(b['text']) for b in toc}
    toc_norm = {t for t in toc_norm if t}
    body_norm = {norm(b['text']) for b in flat}
    toc_matched = sum(1 for t in toc_norm if t in body_norm)
    toc_match_ratio = (toc_matched / len(toc_norm)) if toc_norm else 0.0

    # custom-style: style tên lạ, lặp nhiều, block ngắn, VÀ định dạng lệch baseline (spec 4.1)
    st_ct = Counter(b['style'] for b in blocks
                    if b['style'] and not HEADING_STYLES.match(b['style'])
                    and b['style'] not in BODY_LIKE_STYLES)
    custom = []
    for s, c in st_ct.items():
        if c < 5:
            continue
        members = [b for b in blocks if b['style'] == s]
        avg_len = sum(len(b['text']) for b in members) / c
        if avg_len >= 90:
            continue
        dev_ratio = sum(1 for b in members if deviates(b)) / c
        if dev_ratio < 0.6:      # phải lệch baseline RÕ, không chỉ vài lần tình cờ
            continue
        custom.append(s)
    r_custom = (sum(st_ct[s] for s in custom) / len(blocks)) if blocks else 0.0

    # format-driven: khối NGẮN lệch baseline (đậm khác, hoặc cỡ chữ lệch >=1pt) — spec 4.1/5.4.
    # Đếm theo ĐỘ PHỦ (>=3 khối), không theo sự TỒN TẠI (any) — một khối lệch đơn lẻ không
    # đủ để kết luận cả tài liệu dùng định dạng để đánh dấu heading (bài học ở handoff.md).
    shorts = [b for b in flat if len(b['text']) < 90]
    deviant = [b for b in shorts if deviates(b)]
    d_format = len(deviant) >= 3

    out['r_numpr'] = round(r_numpr, 3)
    out['r_typed'] = round(r_typed, 3)
    out['r_vnadmin'] = round(r_vnadmin, 3)
    out['r_legal'] = round(r_legal, 3)
    out['r_custom'] = round(r_custom, 3)
    out['c_depth'] = round(c_depth, 3)
    out['toc_match_ratio'] = round(toc_match_ratio, 3)
    out['n_deviant_short'] = len(deviant)

    # ---- cây quyết định (spec 4.2, với 2 sửa đã đo và ghi chú — xem HANDOFF/TODO)
    # outlineLvl chỉ đáng tin khi PHỦ phần lớn heading ước tính, không phải khi Word
    # gán lẻ tẻ vài block (đo được: 8/900 và 2/273) — spec §4.2 gốc dùng "r_outline > 0",
    # đã sửa thành ngưỡng độ phủ theo bài học ghi trong handoff.md.
    est_head = max(len(hs), vn_marks, out['n_numpr'], 1)
    out['r_outline'] = round(r_outline_n / est_head, 3)
    if r_outline_n >= 5 and r_outline_n / est_head >= 0.5:
        mode = 'outlinelvl-driven'
    elif len(toc) >= 5 and toc_match_ratio >= 0.8:
        mode = 'toc-anchored'
    elif r_legal >= 0.05 and mk['dieu'] >= 3:
        mode = 'vn-legal'
    elif r_vnadmin >= 0.5:
        mode = 'vn-administrative'
    elif r_typed >= 0.6 and c_depth >= 0.9:
        mode = 'typed-numbering'
    elif r_numpr >= 0.20:
        mode = 'numpr-driven'
    elif r_custom >= 0.5:
        mode = 'custom-style'
    elif d_format:
        mode = 'format-driven'
    else:
        mode = 'UNCLASSIFIED'          # <-- con số cần đếm
    out['mode'] = mode

    # cờ cảnh báo
    fl = []
    if counts['n_del']:
        fl.append('tracked_delete')
    if counts['n_ins']:
        fl.append('tracked_insert')
    if counts['n_sdt']:
        fl.append('sdt')
    if counts['n_field']:
        fl.append('field')
    if counts['n_textbox']:
        fl.append('textbox')
    if counts['n_sect'] > 5:
        fl.append('many_sections')
    if out['n_table_block'] / max(len(blocks), 1) > 0.5:
        fl.append('table_heavy')
    if out['n_bare_head']:
        fl.append('two_line_heading')
    if out['n_corrupt']:
        fl.append('corrupt_paragraph')
    if len(blocks) < 50 and counts['n_drawing'] > 5:
        fl.append('maybe_scanned')
    out['flags'] = '|'.join(fl)
    out.update(counts)
    return out


COLS = ['file', 'status', 'mode', 'flags', 'r_outline', 'n_block', 'n_flat', 'n_table_block',
        'n_textbox_block', 'n_corrupt', 'n_heading_style', 'n_toc', 'toc_match_ratio',
        'n_outlinelvl', 'n_numpr', 'n_center', 'n_bare_head', 'n_deviant_short',
        'r_numpr', 'r_typed', 'c_depth', 'r_vnadmin', 'r_legal', 'r_custom',
        'sample_markers', 'n_ins', 'n_del', 'n_sdt', 'n_field', 'n_textbox',
        'n_sect', 'n_tbl', 'n_drawing', 'error']

MIN_BLOCKS = 5   # dưới ngưỡng này: không đủ chữ để phân loại có căn cứ (spec 13.2 — PDF quét)


def main():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')

    ap = argparse.ArgumentParser()
    ap.add_argument('folder')
    ap.add_argument('--csv', default='tier1_thongke.csv')
    ap.add_argument('--recursive', action='store_true')
    ap.add_argument('--anonymize', action='store_true',
                    help='thay tên file bằng mã băm (khi cần gửi CSV đi)')
    a = ap.parse_args()

    root = Path(a.folder)
    files = sorted(root.rglob('*.docx') if a.recursive else root.glob('*.docx'))
    files = [f for f in files if not f.name.startswith('~$')]
    if not files:
        print('Không tìm thấy .docx nào trong', root)
        return

    rows, modes, flags = [], Counter(), Counter()
    for k, f in enumerate(files, 1):
        name = f.name
        if a.anonymize:
            import hashlib
            name = 'doc_' + hashlib.sha256(name.encode()).hexdigest()[:10]
        row = {c: '' for c in COLS}
        row['file'] = name
        try:
            blocks, _style_map, _doc_defaults = canon.parse_docx(f)
            counts = raw_structure_counts(f)
            if len(blocks) < MIN_BLOCKS:
                row['status'], row['n_block'] = 'insufficient_text', len(blocks)
            else:
                row.update(classify(blocks, counts))
                row['status'] = 'ok'
        except zipfile.BadZipFile:
            row['status'], row['error'] = 'corrupt_or_encrypted', 'không mở được ZIP'
        except Exception as e:
            row['status'], row['error'] = 'error', f'{type(e).__name__}: {e}'[:180]
            if os.environ.get('DEBUG'):
                traceback.print_exc()
        rows.append(row)
        modes[row.get('mode') or row['status']] += 1
        for fl in (row.get('flags') or '').split('|'):
            if fl:
                flags[fl] += 1
        print(f'[{k}/{len(files)}] {name[:52]:52} {row.get("mode") or row["status"]}')

    with open(a.csv, 'w', newline='', encoding='utf-8-sig') as fh:
        w = csv.DictWriter(fh, fieldnames=COLS)
        w.writeheader()
        for r in rows:
            w.writerow({c: r.get(c, '') for c in COLS})

    n = len(rows)
    print('\n' + '=' * 58)
    print(f'{n} file\n')
    print('PHÂN BỐ CHẾ ĐỘ')
    for m, c in modes.most_common():
        bar = '█' * int(c / n * 34)
        print(f'  {m:22} {c:4}  {c/n*100:5.1f}%  {bar}')
    unc = modes.get('UNCLASSIFIED', 0)
    print(f'\n>>> KHÔNG PHÂN LOẠI ĐƯỢC: {unc}/{n} = {unc/n*100:.1f}%')
    print('    (< 10% : spec gần đủ | > 30% : còn thiếu chế độ lớn)')
    if flags:
        print('\nCỜ CẢNH BÁO')
        for f_, c in flags.most_common():
            print(f'  {f_:22} {c:4}  {c/n*100:5.1f}%')
    print(f'\n-> {a.csv}')


if __name__ == '__main__':
    main()

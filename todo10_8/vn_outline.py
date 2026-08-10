#!/usr/bin/env python3
"""
Trích outline heading cho văn bản hành chính / pháp quy Việt Nam.

Sửa so với bản trước:
  1. Suy cấp theo NGỮ CẢNH CHA gần nhất, không gán cứng theo loại ký hiệu
  2. Bảng chữ cái tiếng Việt (a ă â b c d đ e ê g...) cho cấp "Điểm"
  3. Ghép heading trải 2 paragraph (Chương/Phần/Mục theo Nghị định 30/2020)
  4. Tách heading/body theo mẫu payload, không cắt máy móc tại dấu ':'
  5. Ký hiệu gõ tay thắng pStyle khi mâu thuẫn
  6. Không loại block chỉ vì dài — chỉ hạ confidence + cờ needs_split
  7. Bộ điểm §5.4 cho block KHÔNG có ký hiệu/pStyle/từ khóa nào bám được — tài
     liệu chế độ format-driven/semantic-only (phần lớn corpus thật đo được)
     không có luật ứng viên nào trước bản sửa này, ra outline rỗng.
  8. Loại block trong textbox khỏi luồng chính (spec 3.2.4), loại block corrupt (X1)

Dùng:
    python vn_outline.py blocks.json
    python vn_outline.py blocks.json --json out.json --tables
"""

import argparse
import json
import re
import unicodedata
from collections import Counter

# ---------------------------------------------------------------- bảng chữ cái
# Bảng chữ cái tiếng Việt đầy đủ (Nghị định 30/2020 viện dẫn)
VN_ALPHA = 'aăâbcdđeêghiklmnoôơpqrstuưvxy'
# Chuỗi THỰC TẾ tác giả dùng: bỏ các biến thể dấu (ă â ê ô ơ ư), giữ đ
VN_SEQ_COMMON = 'abcdđeghiklmnopqrstuvxy'
VN_IDX = {c: i for i, c in enumerate(VN_ALPHA)}
VN_IDX_COMMON = {c: i for i, c in enumerate(VN_SEQ_COMMON)}


def vn_index(c):
    return VN_IDX.get(c.lower())


def alpha_is_next(prev_ch, cur_ch):
    """Hợp lệ nếu liền kề theo BẤT KỲ chuỗi nào (đầy đủ hoặc rút gọn)."""
    for idx in (VN_IDX, VN_IDX_COMMON):
        a, b = idx.get(prev_ch.lower()), idx.get(cur_ch.lower())
        if a is not None and b is not None and b == a + 1:
            return True
    return False


# ------------------------------------------------------------------ số La Mã
def roman_to_int(s):
    vals = {'I': 1, 'V': 5, 'X': 10, 'L': 50, 'C': 100, 'D': 500, 'M': 1000}
    s = (s or '').upper()
    if not s or any(ch not in vals for ch in s):
        return None
    total, prev = 0, 0
    for ch in reversed(s):
        v = vals[ch]
        total += -v if v < prev else v
        prev = max(prev, v)
    return total


# --------------------------------------------------------------- lớp ký hiệu
# rank nhỏ = cấp cao. Thứ tự kiểm tra: cụ thể nhất -> tổng quát nhất,
# để '3.1.' không bị luật '^\d+\.' bắt trước.
MARKERS = [
    ('phan',    0, re.compile(r'^\s*(?:PHẦN|Phần)\s+(?:thứ\s+)?([IVXLCM]+|\d+)\b')),
    ('chuong',  1, re.compile(r'^\s*(?:CHƯƠNG|Chương)\s+([IVXLCM]+|\d+)\b')),
    ('muc',     2, re.compile(r'^\s*(?:MỤC|Mục)\s+(\d+)\b')),
    ('tieumuc', 3, re.compile(r'^\s*(?:TIỂU MỤC|Tiểu mục)\s+(\d+)\b')),
    ('dieu',    4, re.compile(r'^\s*(?:ĐIỀU|Điều)\s+(\d+)\s*[\.:]?\s')),
    ('roman',   5, re.compile(r'^\s*([IVXLCM]+)\s*[\.\)]\s*(?=\S)')),
    ('dec2',    7, re.compile(r'^\s*(\d{1,2}(?:\.\d{1,2})+)\s*\.?\s')),
    ('num',     6, re.compile(r'^\s*(\d{1,2})\s*[\.\)]\s*(?!\d)')),
    ('alpha',   8, re.compile(r'^\s*([' + VN_ALPHA + r'])\s*[\.\)]\s')),
    ('bullet',  9, re.compile(r'^\s*([-+*•])\s+(?=\S)')),
]

CAPTION = re.compile(r'^\s*(Bảng|Hình|Biểu\s*đồ|Sơ\s*đồ|Table|Figure)\s*[\dIVX]', re.I)

KEYWORD = re.compile(
    r'^\s*(MỤC LỤC|DANH MỤC[^\n]{0,40}|LỜI (CAM ĐOAN|CẢM ƠN|MỞ ĐẦU|NÓI ĐẦU)'
    r'|MỞ ĐẦU|ĐẶT VẤN ĐỀ|TỔNG QUAN|KẾT LUẬN|KIẾN NGHỊ|TÀI LIỆU THAM KHẢO'
    r'|PHỤ LỤC|TÓM TẮT|ABSTRACT|NHẬN XÉT CHUNG)\s*$', re.I)

INLINE_LABEL = re.compile(
    r'^\s*(Nhận xét|Ghi chú|Lưu ý|Đánh giá|Thời gian|Địa điểm'
    r'|Thành phần|Chủ trì|Thư ký|Căn cứ)\s*:', re.I)

# mẫu "payload" — nơi phần NỘI DUNG bắt đầu. CẤU HÌNH THEO DOMAIN.
PAYLOAD_DEFAULT = re.compile(
    r'(?='
    r'\d+/\d+'
    r'|\d[\d\.,]*\s*(?:tốp|tàu|chiếc|lượt|l/c|l/m|giàn|công dân|đơn vị|người|vụ|hồ sơ|trường hợp)'
    r'|\((?:tăng|giảm|như ngày)'
    r'|Bình thường'
    r'|Không ghi nhận'
    r')')

BARE_HEAD = re.compile(
    r'^\s*(?:PHẦN|Phần|CHƯƠNG|Chương|MỤC|Mục|TIỂU MỤC|Tiểu mục)'
    r'\s+(?:thứ\s+)?[IVXLCM\d]+\s*[\.:]?\s*$')


def norm(s):
    return re.sub(r'\s+', ' ', unicodedata.normalize('NFC', s or '')).strip()


def match_marker(text):
    """(tên, rank, chuỗi số, vị trí kết thúc ký hiệu) hoặc None."""
    for name, rank, rx in MARKERS:
        m = rx.match(text)
        if m:
            return name, rank, m.group(1), m.end()
    return None


# ------------------------------------------------- ghép heading trải 2 paragraph
def merge_two_line_headings(blocks):
    """Nghị định 30/2020: 'Chương II' một dòng, tiêu đề IN HOA dòng kế tiếp."""
    out, i, merged = [], 0, 0
    while i < len(blocks):
        b = blocks[i]
        t = b.get('text', '')
        if i + 1 < len(blocks) and BARE_HEAD.match(t):
            nxt = blocks[i + 1]
            nt = (nxt.get('text') or '').strip()
            looks_title = (
                nt and len(nt) <= 200
                and (nt.isupper() or nxt.get('jc') == 'center' or nxt.get('bold'))
                and not match_marker(nt)
            )
            if looks_title:
                nb = dict(b)
                nb['text'] = f"{t.rstrip(' .:')} — {nt}"
                nb['merged_from'] = [b.get('block_id'), nxt.get('block_id')]
                nb['bold'] = b.get('bold') or nxt.get('bold')
                out.append(nb)
                i += 2
                merged += 1
                continue
        out.append(b)
        i += 1
    return out, merged


# ------------------------------------------------------------ tách heading/body
def split_heading_body(text, marker_end, payload_re):
    """Tìm nơi NỘI DUNG bắt đầu, không tìm nơi tiêu đề kết thúc."""
    rest = text[marker_end:]
    m = payload_re.search(rest)
    cut_p = m.start() if m else None
    c = rest.find(':')
    cut_c = c if c >= 0 else None
    cuts = [x for x in (cut_p, cut_c) if x is not None and x > 0]
    if not cuts:
        return rest.strip(' .:'), None, 'toàn bộ là heading'
    cut = min(cuts)
    return rest[:cut].strip(' .:'), rest[cut:].strip(' :').strip(), \
        ('payload' if cut == cut_p else 'dấu :')


# ------------------------------------------------------------------ ứng viên
def is_candidate(b, baseline):
    t = (b.get('text') or '').strip()
    if not t or b.get('corrupt'):
        return False, []
    if CAPTION.match(t):
        return False, ['caption']
    why = []
    mk = match_marker(t)
    if mk:
        why.append('ký hiệu:' + mk[0])
    if KEYWORD.match(t):
        why.append('từ khóa')
    if (b.get('style') or '').startswith(('Heading', 'Title')):
        why.append('pStyle')
    if b.get('jc') == 'center' and b.get('bold'):
        why.append('canh giữa+đậm')
    if b.get('bold') and not baseline[0]:
        why.append('đậm')
    return (len(why) > 0), why


# ---------------------------------------------------- bộ điểm §5.4 (format-driven)
FORMAT_SCORE_THRESHOLD = 0.62
_TYPED_LEAD = re.compile(r'^\d+(\.\d+)+')
_END_PUNCT = re.compile(r'[.!?;,]$')


def format_score(b, baseline_bold, baseline_size, next_len):
    """Điểm cho ứng viên KHÔNG có ký hiệu/pStyle/từ khóa nào bám được — nguồn
    ứng viên duy nhất cho tài liệu format-driven/semantic-only (spec 5.4)."""
    t = (b.get('text') or '').strip()
    score = 0.0
    size = b.get('size_pt')
    if size and baseline_size and size > baseline_size:
        score += 0.45
    if b.get('bold') and not baseline_bold:
        score += 0.35
    if _TYPED_LEAD.match(t):
        score += 0.45
    if KEYWORD.match(t):
        score += 0.45
    if len(t) <= 90:
        score += 0.12
    if not _END_PUNCT.search(t):
        score += 0.12
    if t.isupper() and len(t) > 6:
        score += 0.15
    if b.get('jc') == 'center':
        score += 0.10
    if next_len > 200 and len(t) < 90:
        score += 0.15
    if b.get('in_table'):
        score -= 0.50
    return score


# ----------------------------------------------------------- suy cấp theo cha
def assign_levels(cands):
    """SỬA CHÍNH: cấp = cấp của cha gần nhất + 1, không gán cứng theo ký hiệu."""
    stack = []                       # [(rank, level)]
    for c in cands:
        rank = c['rank']
        if rank is None:             # từ khóa cấu trúc -> luôn cấp 1
            stack = [(-1, 1)]
            c['level'] = 1
            continue
        while stack and stack[-1][0] >= rank:
            stack.pop()
        c['level'] = (stack[-1][1] + 1) if stack else 1
        stack.append((rank, c['level']))
    return cands


# ------------------------------------------------------------------ validator
def seq_value(marker, raw):
    if marker == 'alpha':
        return vn_index(raw)
    if marker in ('roman', 'phan', 'chuong') and not raw.isdigit():
        return roman_to_int(raw)
    if marker == 'dec2':
        return int(raw.split('.')[-1])
    return int(raw) if raw.isdigit() else None


def check_sequence(cands):
    """Chuỗi đánh số liên tục trong từng nhóm anh em.

    dec2 (x.y) gom thêm theo TIỀN TỐ: 5.1,5.2,5.3 là một nhóm; 4.x là nhóm khác.
    alpha chấp nhận cả chuỗi tiếng Việt đầy đủ lẫn chuỗi rút gọn thực tế.
    """
    issues, groups = [], {}
    for c in cands:
        prefix = c['num'].rsplit('.', 1)[0] if c['marker'] == 'dec2' and '.' in c['num'] else ''
        groups.setdefault((c.get('parent_id'), c['marker'], prefix), []).append(c)

    for (_pid, mk, _pf), items in groups.items():
        if mk in (None, 'bullet') or len(items) < 2:
            continue
        prev_raw = prev = None
        for c in items:
            raw = c['num']
            if mk == 'alpha':
                if prev_raw is not None and not alpha_is_next(prev_raw, raw):
                    issues.append({'type': 'sequence_gap', 'marker': mk,
                                   'block_id': c['block_id'],
                                   'expected': 'sau %r' % prev_raw, 'found': raw,
                                   'text': c['heading'][:60]})
                prev_raw = raw
                continue
            cur = seq_value(mk, raw)
            if cur is not None and prev is not None and cur != prev + 1:
                issues.append({'type': 'sequence_gap', 'marker': mk,
                               'block_id': c['block_id'], 'expected': prev + 1,
                               'found': raw, 'text': c['heading'][:60]})
            if cur is not None:
                prev = cur
    return issues


def check_sibling_symmetry(cands):
    issues, groups = [], {}
    for c in cands:
        groups.setdefault((c.get('parent_id'), c['marker']), []).append(c)
    for _key, items in groups.items():
        if len(items) < 3:
            continue
        lens = sorted(len(c['heading']) for c in items)
        med = lens[len(lens) // 2] or 1
        for c in items:
            if len(c['heading']) > med * 3:
                issues.append({'type': 'sibling_asymmetry', 'block_id': c['block_id'],
                               'len': len(c['heading']), 'median': med,
                               'text': c['heading'][:60]})
    return issues


# ---------------------------------------------------------------------- chính
def build_outline(blocks, payload_re=PAYLOAD_DEFAULT, allow_table=False):
    blocks = [b for b in blocks if (b.get('text') or '').strip()]
    blocks = [b for b in blocks if not b.get('in_textbox')]   # spec 3.2.4: stream riêng
    blocks = [b for b in blocks if not b.get('corrupt')]       # spec X1
    if not allow_table:
        blocks = [b for b in blocks if not b.get('in_table')]
    blocks, n_merged = merge_two_line_headings(blocks)

    longs = [b for b in blocks if len(b.get('text', '')) > 200]
    bolds = [bool(b.get('bold')) for b in longs]
    baseline_bold = Counter(bolds).most_common(1)[0][0] if bolds else False
    sizes = [b.get('size_pt') for b in longs if b.get('size_pt')]
    baseline_size = Counter(sizes).most_common(1)[0][0] if sizes else None
    baseline = (baseline_bold, baseline_size)

    seen = Counter(norm(b['text']) for b in blocks)

    cands, claimed = [], set()
    for idx, b in enumerate(blocks):
        ok, why = is_candidate(b, baseline)
        if not ok:
            continue
        claimed.add(idx)
        t = b['text'].strip()
        mk = match_marker(t)
        if mk:
            name, rank, num, end = mk
            heading, body, src = split_heading_body(t, end, payload_re)
            prefix = t[:end].strip()
        else:
            name, rank, num, prefix = None, None, '', ''
            heading, body, src = t, None, 'từ khóa'

        conf, flags = (0.9 if mk else 0.7), []
        if len(heading) > 120:
            conf -= 0.1
            flags.append('needs_split')
        if seen[norm(t)] > 2:
            conf -= 0.2
            flags.append('repeat_x%d' % seen[norm(t)])
        if INLINE_LABEL.match(t):
            conf -= 0.25
            flags.append('inline_label')
        if b.get('in_table'):
            conf -= 0.15
            flags.append('in_table')
        if mk and 'pStyle' in why:
            flags.append('marker_over_style')

        cands.append({'_idx': idx, 'block_id': b.get('block_id'), 'marker': name, 'rank': rank,
                      'num': num, 'prefix': prefix, 'heading': heading or t,
                      'body': body, 'split_src': src,
                      'confidence': round(max(conf, 0.0), 2),
                      'flags': flags, 'why': why,
                      'merged_from': b.get('merged_from')})

    # ---- bộ điểm §5.4: CHỈ cho block chưa được ký hiệu/pStyle/từ khóa nhận —
    # nguồn ứng viên duy nhất khi tài liệu không còn tín hiệu cấu trúc nào khác
    # (format-driven/semantic-only — đa số corpus thật đo được, xem TODO/HANDOFF).
    scored = []
    for idx, b in enumerate(blocks):
        if idx in claimed:
            continue
        t = (b.get('text') or '').strip()
        if not t or CAPTION.match(t) or b.get('in_table'):
            continue
        next_len = len(blocks[idx + 1].get('text') or '') if idx + 1 < len(blocks) else 0
        score = format_score(b, baseline_bold, baseline_size, next_len)
        if score >= FORMAT_SCORE_THRESHOLD:
            scored.append((idx, b, score))

    # cấp cho ứng viên theo điểm = thứ hạng cỡ chữ giảm dần (đậm trước khi bằng cỡ) —
    # cùng nguyên tắc spec dùng cho custom-style (5.2 B3). Rank bắt đầu từ 20 để luôn
    # lồng dưới ký hiệu số/La Mã/chữ cái (rank 0-9) nếu tài liệu có cả hai loại tín hiệu.
    sig_rank = {}
    sigs = sorted({(b.get('size_pt') or 0, bool(b.get('bold'))) for _, b, _ in scored},
                  key=lambda s: (-s[0], not s[1]))
    for r, sig in enumerate(sigs):
        sig_rank[sig] = 20 + r

    for idx, b, score in scored:
        t = b['text'].strip()
        sig = (b.get('size_pt') or 0, bool(b.get('bold')))
        flags = []
        if seen[norm(t)] > 2:
            flags.append('repeat_x%d' % seen[norm(t)])
        if INLINE_LABEL.match(t):
            flags.append('inline_label')
        cands.append({'_idx': idx, 'block_id': b.get('block_id'), 'marker': 'format',
                      'rank': sig_rank[sig], 'num': '', 'prefix': '', 'heading': t,
                      'body': None, 'split_src': None,
                      'confidence': round(min(score, 1.0), 2),
                      'flags': flags, 'why': ['bộ điểm %.2f' % score],
                      'merged_from': None})

    cands.sort(key=lambda c: c['_idx'])
    assign_levels(cands)

    stack = []
    for c in cands:
        while stack and stack[-1]['level'] >= c['level']:
            stack.pop()
        c['parent_id'] = stack[-1]['block_id'] if stack else None
        stack.append(c)

    return cands, check_sequence(cands) + check_sibling_symmetry(cands), n_merged


def main():
    import sys
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')

    ap = argparse.ArgumentParser()
    ap.add_argument('blocks', help='.docx (đọc trực tiếp) hoặc blocks.json (đã dựng sẵn bằng canon.py)')
    ap.add_argument('--json')
    ap.add_argument('--tables', action='store_true')
    a = ap.parse_args()

    if a.blocks.lower().endswith('.docx'):
        import canon
        blocks, _sm, _dd = canon.parse_docx(a.blocks)
    else:
        with open(a.blocks, encoding='utf-8') as fh:
            blocks = json.load(fh)
    cands, issues, n_merged = build_outline(blocks, allow_table=a.tables)

    for c in cands:
        line = '  ' * (c['level'] - 1) + '#' * min(c['level'], 6) + ' '
        if c['prefix']:
            line += c['prefix'] + ' '
        line += c['heading'][:66]
        tail = []
        if c['body']:
            tail.append('body[%s]' % c['split_src'])
        if c['flags']:
            tail.append(','.join(c['flags']))
        print('%-84s%s' % (line, ('· ' + ' | '.join(tail)) if tail else ''))

    print('\n%d heading | cấp: %s | ghép 2-dòng: %d'
          % (len(cands), dict(sorted(Counter(c['level'] for c in cands).items())), n_merged))
    if issues:
        print('\n%d cảnh báo validator:' % len(issues))
        for i in issues[:20]:
            print('  [%s] %s: %s' % (i['type'], i.get('block_id'), i.get('text', '')))
            if i['type'] == 'sequence_gap':
                print('      chờ %r nhưng gặp %r (%s)' % (i['expected'], i['found'], i['marker']))

    if a.json:
        with open(a.json, 'w', encoding='utf-8') as fh:
            json.dump({'outline': cands, 'issues': issues}, fh, ensure_ascii=False, indent=1)
        print('\n-> ' + a.json)


if __name__ == '__main__':
    main()

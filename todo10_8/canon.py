"""Canonical block model cho heading detection.
Gộp run theo ngữ nghĩa: bỏ qua các thuộc tính vi mô không ảnh hưởng nhận diện heading.
Resolve định dạng hiệu dụng (bold/size) từ styles.xml khi run không tự khai báo (spec 3.5).
"""
import json
import re
import stat
import unicodedata
import zipfile
from lxml import etree

W = '{http://schemas.openxmlformats.org/wordprocessingml/2006/main}'
XMLSPACE = '{http://www.w3.org/XML/1998/namespace}space'

# Thuộc tính rPr KHÔNG ảnh hưởng tới việc nhận diện heading -> bỏ khi so sánh
IGNORE_RPR = {'spacing', 'kern', 'position', 'noProof', 'lang', 'rFonts', 'w', 'rtl', 'em', 'shd'}
# Thuộc tính CÓ ý nghĩa -> giữ để so sánh và làm metadata
KEEP_RPR = {'b', 'i', 'u', 'sz', 'szCs', 'color', 'caps', 'smallCaps', 'strike', 'vertAlign'}

# Container không được đưa nội dung vào luồng chính (spec 3.2.1, 3.2.4)
_DROP_ANCESTOR = {'del', 'txbxContent'}

# parser an toàn: tắt entity resolution & network (chống XXE, billion-laughs) — spec 3.1
_SAFE = etree.XMLParser(resolve_entities=False, no_network=True, huge_tree=False)


def safe_fromstring(data):
    return etree.fromstring(data, parser=_SAFE)


def rpr_signature(r):
    rPr = r.find(W + 'rPr')
    if rPr is None:
        return ()
    sig = []
    for c in rPr:
        name = etree.QName(c).localname
        if name in IGNORE_RPR:
            continue
        val = c.get(W + 'val')
        if name in ('b', 'i', 'u', 'caps', 'smallCaps', 'strike'):
            if val in ('0', 'false', 'none'):
                continue
            sig.append((name, '1'))
        else:
            sig.append((name, val or ''))
    return tuple(sorted(sig))


def run_explicit_bold_size(r):
    """Bold/size tường minh khai báo trên CHÍNH run này (None = không khai báo)."""
    rPr = r.find(W + 'rPr')
    if rPr is None:
        return None, None
    bold = None
    b = rPr.find(W + 'b')
    if b is not None:
        bold = b.get(W + 'val') not in ('0', 'false', 'none')
    size = None
    sz = rPr.find(W + 'sz')
    if sz is not None:
        v = sz.get(W + 'val')
        if v and v.isdigit():
            size = int(v) / 2.0  # half-point -> pt
    return bold, size


def run_text(r):
    """Trả về text đúng như Word render (áp dụng quy tắc xml:space)."""
    out = []
    for node in r:
        n = etree.QName(node).localname
        if n == 't':
            txt = node.text or ''
            preserve = node.get(XMLSPACE) == 'preserve'
            if not preserve:
                # w:t toàn khoảng trắng vẫn render thành 1 dấu cách (kiểm chứng bằng render PDF);
                # chỉ cắt khoảng trắng rìa khi phần tử có nội dung khác.
                txt = ' ' if txt and not txt.strip() else txt.strip(' \t\r\n')
            out.append(txt)
        elif n == 'br':
            out.append('\x0b')   # marker line-break
        elif n == 'tab':
            out.append('\t')
    return ''.join(out)


def _content_runs(p):
    """Yield (run, pending_insert, ins_id) cho mọi w:r thuộc VỀ đoạn p này.

    Duyệt ĐỆ QUY (không chỉ con trực tiếp) để không bỏ sót run nằm trong
    w:ins / w:hyperlink / w:sdt / w:smartTag / w:fldSimple (spec 3.2.1, 3.2.2).
    Loại run nằm trong w:del (đã xoá, chưa accept — spec 3.2.1) và run thuộc
    một w:p lồng bên trong w:txbxContent của CHÍNH đoạn này (spec 3.2.4).
    """
    for r in p.iter(W + 'r'):
        skip = False
        pending_insert = False
        ins_id = None
        node = r.getparent()
        while node is not None and node is not p:
            tag = etree.QName(node).localname
            if tag in _DROP_ANCESTOR or tag == 'p':
                # 'p' lồng bên trong = đoạn khác (vd. paragraph trong textbox
                # được neo ở run của đoạn này) -> không thuộc p, bỏ qua.
                skip = True
                break
            if tag == 'ins':
                pending_insert = True
                ins_id = node.get(W + 'id')
            node = node.getparent()
        if skip:
            continue
        yield r, pending_insert, ins_id


def merge_semantic(p):
    """Gộp các run liền kề có chữ ký định dạng ngữ nghĩa giống nhau.

    Không gộp qua ranh giới w:ins khác nhau (mỗi lần chèn là một sửa đổi
    riêng biệt, gộp lại sẽ hợp nhất các lần sửa thành một — spec 3.2.1).
    """
    segs = []
    for r, pending_insert, ins_id in _content_runs(p):
        t = run_text(r)
        if not t:
            continue
        sig = rpr_signature(r)
        key = (sig, pending_insert, ins_id)
        bold_x, size_x = run_explicit_bold_size(r)
        if segs and segs[-1]['key'] == key:
            segs[-1]['text'] += t
        else:
            segs.append({'key': key, 'sig': sig, 'text': t,
                         'pending_insert': pending_insert,
                         'bold_explicit': bold_x, 'size_explicit': size_x})
    return segs


def is_doubled(t, thresh=0.55):
    """Phát hiện paragraph hỏng kiểu 'HHììnnhh' — ký tự lặp liên tiếp bất thường.

    Chỉ xét CHỮ CÁI. Dấu chấm lấp chỗ trống trong mẫu biểu hành chính
    ('Ngày......tháng......năm......') cũng thoả 'cặp giống nhau liên tiếp'
    nhưng không phải lỗi — đo được gây dương giả trên 20/95 tài liệu corpus.
    """
    c = [ch for ch in t if ch.isalpha()]
    if len(c) < 12:
        return False
    pairs = sum(1 for i in range(0, len(c) - 1, 2) if c[i].lower() == c[i + 1].lower())
    return pairs / (len(c) // 2) >= thresh


# ------------------------------------------------------------- styles.xml
def parse_styles(styles_bytes):
    """Trả về (style_map, doc_defaults). style_map[styleId] = {basedOn, bold, size_pt}.

    bold/size = None nghĩa là style đó không tự khai báo (phải đi tiếp basedOn).
    """
    style_map = {}
    doc_defaults = (None, None)
    if not styles_bytes:
        return style_map, doc_defaults
    try:
        root = safe_fromstring(styles_bytes)
    except etree.XMLSyntaxError:
        return style_map, doc_defaults

    dd = root.find(W + 'docDefaults')
    if dd is not None:
        rprd = dd.find(W + 'rPrDefault')
        rpr = rprd.find(W + 'rPr') if rprd is not None else None
        if rpr is not None:
            b = rpr.find(W + 'b')
            bold = (b.get(W + 'val') not in ('0', 'false', 'none')) if b is not None else None
            sz = rpr.find(W + 'sz')
            size = int(sz.get(W + 'val')) / 2.0 if (sz is not None and (sz.get(W + 'val') or '').isdigit()) else None
            doc_defaults = (bold, size)

    for st in root.findall(W + 'style'):
        if st.get(W + 'type') != 'paragraph':
            continue
        sid = st.get(W + 'styleId')
        if not sid:
            continue
        based = st.find(W + 'basedOn')
        based_on = based.get(W + 'val') if based is not None else None
        rpr = st.find(W + 'rPr')
        bold = size = None
        if rpr is not None:
            b = rpr.find(W + 'b')
            if b is not None:
                bold = b.get(W + 'val') not in ('0', 'false', 'none')
            sz = rpr.find(W + 'sz')
            if sz is not None and (sz.get(W + 'val') or '').isdigit():
                size = int(sz.get(W + 'val')) / 2.0
        style_map[sid] = {'basedOn': based_on, 'bold': bold, 'size_pt': size}
    return style_map, doc_defaults


def resolve_style(style_id, style_map):
    """Đi theo chuỗi basedOn, trả (bold, size_pt) — giá trị GẦN style_id nhất thắng."""
    bold = size = None
    sid, seen = style_id, set()
    while sid and sid not in seen:
        seen.add(sid)
        st = style_map.get(sid)
        if not st:
            break
        if bold is None and st.get('bold') is not None:
            bold = st['bold']
        if size is None and st.get('size_pt') is not None:
            size = st['size_pt']
        if bold is not None and size is not None:
            break
        sid = st.get('basedOn')
    return bold, size


def effective_format(pstyle_id, seg_bold_explicit, seg_size_explicit, style_map, doc_defaults):
    """Định dạng hiệu dụng: run tự khai báo thắng, rồi tới style (đệ quy basedOn), rồi docDefaults."""
    bold, size = seg_bold_explicit, seg_size_explicit
    if bold is None or size is None:
        st_bold, st_size = resolve_style(pstyle_id, style_map) if pstyle_id else (None, None)
        if bold is None:
            bold = st_bold
        if size is None:
            size = st_size
    if bold is None:
        bold = doc_defaults[0]
    if size is None:
        size = doc_defaults[1]
    return bool(bold), size


# ------------------------------------------------------------- ingest an toàn
def _reject_unsafe_members(zf):
    """Spec 3.1: từ chối symlink và entry thoát khỏi thư mục đích."""
    for info in zf.infolist():
        mode = info.external_attr >> 16
        if mode and stat.S_ISLNK(mode):
            raise ValueError(f'entry symlink không được phép: {info.filename}')
        name = info.filename.replace('\\', '/')
        if name.startswith('/') or '..' in name.split('/'):
            raise ValueError(f'entry thoát khỏi package: {info.filename}')


def parse_docx(path):
    """Đọc trực tiếp .docx (ZIP) an toàn, trả về (blocks, style_map, doc_defaults)."""
    with zipfile.ZipFile(path) as zf:
        _reject_unsafe_members(zf)
        names = set(zf.namelist())
        if 'word/document.xml' not in names:
            raise ValueError('thiếu word/document.xml')
        doc_bytes = zf.read('word/document.xml')
        styles_bytes = zf.read('word/styles.xml') if 'word/styles.xml' in names else b''
    style_map, doc_defaults = parse_styles(styles_bytes)
    root = safe_fromstring(doc_bytes)
    blocks = _blocks_from_root(root, style_map, doc_defaults)
    return blocks, style_map, doc_defaults


def _blocks_from_root(root, style_map, doc_defaults):
    body = root.find(W + 'body')
    blocks = []
    txbx_ids = {}
    for i, p in enumerate(body.iter(W + 'p')):
        pPr = p.find(W + 'pPr')
        style = numId = ilvl = outline = None
        jc = ind = None
        if pPr is not None:
            st = pPr.find(W + 'pStyle')
            style = st.get(W + 'val') if st is not None else None
            np = pPr.find(W + 'numPr')
            if np is not None:
                n = np.find(W + 'numId')
                l = np.find(W + 'ilvl')
                numId = n.get(W + 'val') if n is not None else None
                ilvl = l.get(W + 'val') if l is not None else None
            j = pPr.find(W + 'jc')
            jc = j.get(W + 'val') if j is not None else None
            d = pPr.find(W + 'ind')
            ind = d.get(W + 'left') if d is not None else None
            o = pPr.find(W + 'outlineLvl')
            outline = o.get(W + 'val') if o is not None else None

        segs = merge_semantic(p)
        raw = ''.join(s['text'] for s in segs)
        norm = re.sub(r'\s+', ' ', unicodedata.normalize('NFC', raw).replace('\x0b', ' ')).strip()
        if not norm:
            continue

        # định dạng hiệu dụng: lấy theo segment KHÔNG rỗng đầu tiên
        seg0 = next((s for s in segs if s['text'].strip()), segs[0] if segs else None)
        bold, size_pt = effective_format(
            style, seg0['bold_explicit'] if seg0 else None,
            seg0['size_explicit'] if seg0 else None, style_map, doc_defaults)
        uniform = len({s['sig'] for s in segs}) == 1
        pending_insert = any(s['pending_insert'] for s in segs)

        in_tbl = False
        in_textbox = False
        stream_id = 'main'
        for a in p.iterancestors():
            tag = etree.QName(a).localname
            if tag == 'tbl':
                in_tbl = True
            if tag == 'txbxContent':
                in_textbox = True
                tid = txbx_ids.setdefault(id(a), len(txbx_ids) + 1)
                stream_id = f'txbx{tid}'
                break

        corrupt = is_doubled(norm)
        blocks.append(dict(
            block_id=f'p{i}', para_index=i, style=style,
            numId=numId, ilvl=ilvl, outline=outline, jc=jc, indent=ind,
            in_table=in_tbl, in_textbox=in_textbox, stream_id=stream_id,
            has_break='\x0b' in raw, uniform_format=uniform,
            bold=bold, size_pt=size_pt, pending_insert=pending_insert,
            corrupt=corrupt, n_segments=len(segs), raw=raw, text=norm))
    return blocks


def parse(path):
    """Tương thích ngược: đọc word/document.xml đã giải nén sẵn (không resolve style)."""
    root = etree.parse(path, parser=_SAFE).getroot()
    return _blocks_from_root(root, {}, (None, None))


if __name__ == '__main__':
    import sys
    for _stream in (sys.stdout, sys.stderr):
        if hasattr(_stream, 'reconfigure'):
            _stream.reconfigure(encoding='utf-8')

    docx_path = sys.argv[1] if len(sys.argv) > 1 else None
    if docx_path:
        bs, _sm, _dd = parse_docx(docx_path)
    else:
        bs = parse('unpacked/word/document.xml')
    with open('blocks.json', 'w', encoding='utf-8') as fh:
        json.dump(bs, fh, ensure_ascii=False, indent=1)
    print(f"Canonical blocks (không rỗng): {len(bs)}")
    print(f"  ngoài bảng   : {sum(1 for b in bs if not b['in_table'])}")
    print(f"  trong textbox: {sum(1 for b in bs if b['in_textbox'])}")
    print(f"  pending_insert: {sum(1 for b in bs if b['pending_insert'])}")
    tot_after = sum(b['n_segments'] for b in bs)
    print(f"  segment sau gộp ngữ nghĩa: {tot_after}")

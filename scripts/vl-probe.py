# -*- coding: utf-8 -*-
"""Do nang luc THI GIAC: Qwen3-VL-8B nhin ANH trang in, co trich duoc de muc + cap khong.

Cham tren dung dap an dong thuan da dung cho moi phep do khac, gioi han o cua so trang da chon,
nen so so sanh truc tiep duoc voi P/R/F1/cap cua pipeline doc OOXML.

Dung llama-server chu khong phai llama-mtmd-cli: cli nap lai 4,7 GB trong so cho MOI trang.
"""
import base64, io, json, os, re, subprocess, sys, time, unicodedata, urllib.request

S = os.environ.get("VL_WORKDIR") or sys.exit("Dat VL_WORKDIR = thu muc chua pages/, vl-truth*.json, llamacpp/")
SERVER = S + "/llamacpp/llama-server.exe"
# CHINH bo trong so da dung cho moi phep do van ban, cong projector cua chinh no. Nho vay phep do
# nay mot bien: cung model, doi TEXT OOXML thanh ANH TRANG IN. Neu doi sang Qwen3-VL-8B thi doi ca
# model lan dau vao, khong ket luan duoc gi.
MODEL = sys.argv[1] if len(sys.argv) > 1 else os.environ["VL_MODEL"]
MMPROJ = sys.argv[2] if len(sys.argv) > 2 else os.environ["VL_MMPROJ"]
URL = "http://127.0.0.1:8077"

# Prompt phai viet CO DAU. Ban dau toi go khong dau cho tien; mo hinh bat chuoc van phong do va tra
# ve "CHUONG 1: CO SO LY LUAN...", nen ca trang 24 bi cham sai — 4 muc dung thanh "thua", 2 muc
# thanh "sot". Loi cua phep do, khong phai cua mo hinh.
PROMPT = (
    "Đây là ảnh một trang in của một khoá luận tiếng Việt.\n"
    "Liệt kê MỌI dòng là ĐỀ MỤC (heading) trên trang này, theo đúng thứ tự từ trên xuống, "
    "chép lại nguyên văn CÓ DẤU.\n"
    "Cấp: 1 = tên chương hoặc mục ngang chương (VD 'CHƯƠNG 1:', 'MỞ ĐẦU', 'KẾT LUẬN'); "
    "2 = mục lớn (VD '1.1.'); 3 = mục con (VD '1.1.1.'); sâu hơn thì 4, 5.\n"
    "Đoạn văn thường, chú thích ảnh/bảng, số trang, tiêu đề chạy đầu-cuối trang KHÔNG phải đề mục.\n"
    'Chỉ trả về JSON: {"headings":[{"text":"...","level":1}]}. Không giải thích gì thêm.'
)


def norm(s):
    """Bo dau tieng Viet truoc khi so khop. Phep do khong duoc phu thuoc vao viec mo hinh chon tra
    ve co dau hay khong — do la van phong dau ra, khong phai noi dung cau tra loi."""
    s = unicodedata.normalize("NFD", (s or "").lower().replace("đ", "d"))
    s = "".join(c for c in s if not unicodedata.combining(c))
    return re.sub(r"[^0-9a-z]+", "", s)


def same(a, b):
    """Hai chuoi da chuan hoa co chi cung mot de muc khong.

    Ban dau dieu kien la `len(k) > 10 and (w[:25] in k or k[:25] in w)`. Nguong 10 loai han moi de
    muc NGAN: "2.3.2. Han che" chuan hoa thanh "232hanche" — 9 ky tu — nen khong bao gio khop duoc
    va bi tinh la duong tinh gia, du no nam ngay trong dap an (doan 1118). Mot nguong do dai dat de
    chong khop bua lai tro thanh nguon sai co he thong.
    """
    if not a or not b or min(len(a), len(b)) < 6:
        return False
    short, long = sorted((a, b), key=len)
    # Chua trong nhau la du: mo hinh hay kem nhan dau muc ("a. Van de..." so voi "Van de..."),
    # va dong in co the bi ngat giua chung. So sanh tien to co do dai co dinh thi hai truong hop do
    # deu truot — da sai that hai lan vi dieu nay.
    return short in long


def post(path, payload, timeout=900):
    req = urllib.request.Request(
        URL + path, data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def wait_ready(proc, limit=600):
    t0 = time.time()
    while time.time() - t0 < limit:
        if proc.poll() is not None:
            raise RuntimeError(f"llama-server thoat som, ma {proc.returncode}")
        try:
            with urllib.request.urlopen(URL + "/health", timeout=5) as r:
                if json.load(r).get("status") == "ok":
                    return time.time() - t0
        except Exception:
            time.sleep(3)
    raise RuntimeError("llama-server khong san sang")


def ask(image_path):
    b64 = base64.b64encode(open(image_path, "rb").read()).decode()
    out = post("/v1/chat/completions", {
        "messages": [{"role": "user", "content": [
            {"type": "image_url", "image_url": {"url": "data:image/png;base64," + b64}},
            {"type": "text", "text": PROMPT}]}],
        # Thinking AN HET ngan sach dau ra: luot dau do 698/700 token completion di vao
        # reasoning_content, content rong tuyet doi tren ca 5 trang. Dung co che ma §24 da do o
        # phia van ban, chi khac la o day no giet ca cau tra loi chu khong chi bot recall.
        "temperature": 0, "max_tokens": 1200,
        "chat_template_kwargs": {"enable_thinking": False},
    })
    m = out["choices"][0]["message"]
    return m.get("content") or m.get("reasoning_content") or ""


def parse(raw):
    m = re.search(r'\{.*?"headings".*\}', raw, re.S)
    if not m:
        return None
    try:
        return json.loads(m.group(0)).get("headings")
    except Exception:
        return None


def main():
    truth = json.load(io.open(S + "/" + os.environ.get("VL_TRUTH", "vl-truth.json"), encoding="utf-8"))
    by_page = {}
    for _, (page, level, text) in truth.items():
        by_page.setdefault(page, []).append((level, text))
    pages = sorted(by_page)

    # Dap an TOAN TAI LIEU, de tach hai loai "thua" khac han nhau:
    #   - mo hinh bia ra mot muc khong co trong dap an  -> duong tinh gia THAT
    #   - mo hinh neu dung mot de muc that, nhung phep DINH VI TRANG cua toi xep no o trang khac
    # Loai thu hai la loi cua thuoc do. Vi du da gap: trang 23 la muc "8. Bo cuc khoa luan", noi
    # LIET KE ten ba chuong trong doan van; phep dinh vi khop text dau tien nen gan ba chuong vao
    # do, roi tinh la mo hinh "sot" o trang 23 va "thua" o trang 24 — hai lan sai cho cung mot muc.
    key_all = json.load(io.open(S + "/vl-key-all.json", encoding="utf-8"))
    global_key = {norm(t): lv for lv, t in key_all.values() if norm(t)}

    proc = subprocess.Popen(
        [SERVER, "-m", MODEL, "--mmproj", MMPROJ, "-ngl", "99", "-c", "8192",
         "--host", "127.0.0.1", "--port", "8077"],
        stdout=subprocess.DEVNULL, stderr=open(S + "/vlserver.log", "wb"))
    try:
        print(f"llama-server san sang sau {wait_ready(proc):.0f}s")
        tp = fp = fn = lvl_ok = offpage = 0
        detail = []
        for page in pages:
            t0 = time.time()
            raw = ask(f"{S}/pages/p{page:03d}.png")
            got = parse(raw)
            secs = time.time() - t0
            want = {norm(t): lv for lv, t in by_page[page]}
            if got is None:
                print(f"tr{page:>3}: KHONG DOC DUOC JSON ({secs:.0f}s) :: {raw.strip()[:150]}")
                fn += len(want)
                continue
            seen = set()
            for h in got:
                k = norm(h.get("text"))
                hit = next((w for w in want if same(w, k)), None)
                if hit and hit not in seen:
                    seen.add(hit)
                    tp += 1
                    if h.get("level") == want[hit]:
                        lvl_ok += 1
                    else:
                        detail.append(f"tr{page} cap {h.get('level')} != {want[hit]}: {h.get('text','')[:40]}")
                else:
                    elsewhere = next((g for g in global_key if same(g, k)), None)
                    if elsewhere:
                        offpage += 1
                        detail.append(f"tr{page} LECH TRANG (de muc that): {h.get('text','')[:46]}")
                    else:
                        fp += 1
                        detail.append(f"tr{page} THUA: {h.get('text','')[:50]}")
            for w, lv in want.items():
                if w not in seen:
                    detail.append(f"tr{page} SOT cap{lv}: {w[:40]}")
            fn += len(want) - len(seen)
            print(f"tr{page:>3}: mo hinh {len(got):>2} muc | dap an {len(want):>2} | trung {len(seen):>2} | {secs:.0f}s")

        P = tp / (tp + fp) * 100 if tp + fp else 0
        R = tp / (tp + fn) * 100 if tp + fn else 0
        F = 2 * P * R / (P + R) if P + R else 0
        print(f"\nQWEN3-VL-8B nhin ANH, {len(pages)} trang, {tp + fn} de muc trong dap an:")
        print(f"  P {P:.1f}%  R {R:.1f}%  F1 {F:.1f}%  dung cap "
              f"{lvl_ok / tp * 100 if tp else 0:.1f}% ({lvl_ok}/{tp})")
        print(f"  ngoai ra {offpage} muc la de muc THAT nhung phep dinh vi trang cua toi xep khac trang")
        print("\nchi tiet:")
        for d in detail[:40]:
            print("  " + d)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except Exception:
            proc.kill()


if __name__ == "__main__":
    main()

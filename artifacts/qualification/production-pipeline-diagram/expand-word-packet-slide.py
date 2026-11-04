from pptx import Presentation
from pptx.util import Pt
from pptx.dml.color import RGBColor

folder = r'C:\DocxHeaderExtractor-a99-closed-loop\artifacts\qualification\production-pipeline-diagram'
prs = Presentation(folder + r'\production-extraction-pipeline-v4.pptx')
slide = prs.slides[3]
packet = '''{
  "instruction": "Extract all genuine structural headings...",
  "stage": "SEGMENT", "unitId": "SEG-000",
  "targetCount": 40,
  "targets": [
    {"alias": "S0001", "text": "MEETING MINUTES"},
    {"alias": "S0002", "text": "Governing Committee..."},
    {"alias": "S0003", "text": "Date: November 21, 2024"},
    {"alias": "S0004", "text": "Opening:"}
  ],
  "context": [],
  "targetFacts": [
    {"alias": "S0001",
     "style": {"bold": true, "allCaps": true},
     "place": {"inTable": false}}
  ],
  "documentStyles": { ... }
}'''

def replace(shape, content, size, font, color, bold=False):
    tf=shape.text_frame; tf.clear(); tf.word_wrap=True
    p=tf.paragraphs[0]; p.text=content; p.font.name=font; p.font.size=Pt(size); p.font.bold=bold; p.font.color.rgb=RGBColor.from_string(color)

for shape in slide.shapes:
    if not shape.has_text_frame: continue
    t=shape.text
    if '"stage": "SEGMENT"' in t:
        replace(shape,packet,11.5,'Consolas','213A35')
    elif t.startswith('40 đoạn cần AI'):
        replace(shape,'40 đoạn cần AI xét; hình bên chỉ hiển thị 4 đoạn đầu.',13,'Aptos','334D46')
    elif t.startswith('Đặc điểm từng đoạn'):
        replace(shape,'Ví dụ S0001: in đậm, viết hoa, không nằm trong bảng.',13,'Aptos','334D46')
    elif t.startswith('Ví dụ S0001:'):
        replace(shape,'Định dạng là bằng chứng để AI cân nhắc, chưa tự quyết định đó là tiêu đề.',12,'Aptos','136B5B',True)
        shape.text_frame.paragraphs[0].alignment=2
slide.notes_slide.notes_text_frame.text='Nguồn: artifacts/qualification/docx-075-bundles/f1b6265172d9b8f5/01-user-packet.json. Packet đã rút gọn; ba dấu chấm thể hiện nội dung bỏ bớt. S0002 nguyên văn dài hơn đoạn trên slide. Instruction trích phần đầu tiếng Anh. 40 targets trong packet, slide thể hiện 4 targets đầu. targetFacts S0001 thực tế còn các trường khác. context thực tế là mảng rỗng.'
out=folder + r'\production-extraction-pipeline-v6.pptx'
prs.save(out)
print(out)

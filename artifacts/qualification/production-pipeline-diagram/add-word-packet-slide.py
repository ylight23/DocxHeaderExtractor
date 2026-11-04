from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR

folder = r'C:\DocxHeaderExtractor-a99-closed-loop\artifacts\qualification\production-pipeline-diagram'
source = folder + r'\production-extraction-pipeline-v3.pptx'
output = folder + r'\production-extraction-pipeline-v4.pptx'
prs = Presentation(source)
slide = prs.slides.add_slide(prs.slide_layouts[0])
def rgb(value): return RGBColor.from_string(value)
def rect(x,y,w,h,color):
 s=slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(x), Inches(y), Inches(w), Inches(h)); s.fill.solid(); s.fill.fore_color.rgb=rgb(color); s.line.fill.background(); return s
def text(x,y,w,h,value,size,color='213A35',bold=False,font='Aptos',align=PP_ALIGN.LEFT):
 box=slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h)); tf=box.text_frame; tf.clear(); tf.word_wrap=True
 tf.margin_left=tf.margin_right=Pt(0); tf.margin_top=tf.margin_bottom=Pt(0); tf.vertical_anchor=MSO_ANCHOR.TOP
 p=tf.paragraphs[0]; p.text=value; p.alignment=align; p.font.name=font; p.font.size=Pt(size); p.font.bold=bold; p.font.color.rgb=rgb(color); return box

slide.background.fill.solid(); slide.background.fill.fore_color.rgb=rgb('F6F8F7')
rect(0,0,13.333,.13,'136B5B')
text(.5,.43,12.1,.52,'Ví dụ packet Word gửi cho AI',25,'132F2C',True)
text(.52,1.02,12.0,.30,'DOCX 075 · SEG-000 · trích nội dung thật, đã rút gọn',13,'526760')
rect(.53,1.62,5.95,4.55,'E8EEEC')
text(.78,1.83,5.48,3.98,'{\n  "stage": "SEGMENT",\n  "unitId": "SEG-000",\n  "targetCount": 40,\n  "targets": [\n    { "alias": "S0001",\n      "text": "MEETING MINUTES" },\n    { "alias": "S0003",\n      "text": "Date: November 21, 2024" }\n  ],\n  "context": [],\n  "targetFacts": [ ... ],\n  "documentStyles": { ... }\n}',13,'213A35',False,'Consolas')
labels=[('targets','40 đoạn cần AI xem và quyết định.'),('context','Đoạn xung quanh để tham khảo; ví dụ này rỗng.'),('targetFacts','Đặc điểm từng đoạn: chữ đậm, viết hoa, vị trí…'),('documentStyles','Các kiểu chữ trong toàn tài liệu để đối chiếu.')]
for i,(heading,body) in enumerate(labels):
 y=1.72+i*.94
 text(6.90,y,5.65,.30,heading,15,'7354A3',True)
 text(6.90,y+.34,5.65,.42,body,14,'334D46')
rect(.53,6.42,12.23,.45,'E4F1EC')
text(.75,6.52,11.8,.22,'Ví dụ S0001: “MEETING MINUTES” được ghi nhận in đậm và viết hoa.',12,'136B5B',True,align=PP_ALIGN.CENTER)
text(.52,7.15,12.0,.20,'PRODUCTION EXTRACTION  /  VÍ DỤ PACKET WORD',8,'60716C')
slide.notes_slide.notes_text_frame.text='Nguồn: artifacts/qualification/docx-075-bundles/f1b6265172d9b8f5/01-user-packet.json. Đây là trích đoạn user packet đã rút gọn, không phải toàn bộ wire request. targets có 40 phần tử; hai phần tử hiển thị không liền nhau; context rỗng. Facts của S0001 có style.bold và style.allCaps là true.'
prs.save(output)
print(output, 'slides:', len(prs.slides))

from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR

folder = r'C:\DocxHeaderExtractor-a99-closed-loop\artifacts\qualification\production-pipeline-diagram'
source = folder + r'\production-extraction-pipeline-v2.pptx'
output = folder + r'\production-extraction-pipeline-v3.pptx'
prs = Presentation(source)
slide = prs.slides.add_slide(prs.slide_layouts[0])

def rgb(value): return RGBColor.from_string(value)
def rect(x, y, w, h, color):
    shape = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(x), Inches(y), Inches(w), Inches(h))
    shape.fill.solid(); shape.fill.fore_color.rgb = rgb(color); shape.line.fill.background()
    return shape
def text(x, y, w, h, value, size, color='213A35', bold=False, align=PP_ALIGN.LEFT):
    box = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = box.text_frame; tf.clear(); tf.word_wrap = True
    tf.margin_left = tf.margin_right = Pt(0); tf.margin_top = tf.margin_bottom = Pt(0)
    tf.vertical_anchor = MSO_ANCHOR.MIDDLE
    p = tf.paragraphs[0]; p.text = value; p.alignment = align
    p.font.name = 'Aptos'; p.font.size = Pt(size); p.font.bold = bold; p.font.color.rgb = rgb(color)
    return box

slide.background.fill.solid(); slide.background.fill.fore_color.rgb = rgb('F6F8F7')
rect(0, 0, 13.333, .13, '136B5B')
text(.50, .43, 12.2, .50, 'Hướng dẫn AI trích xuất tiêu đề', 25, '132F2C', True)
text(.52, 1.02, 12.0, .32, 'Ba phần hướng dẫn phối hợp trong quá trình xử lý tài liệu', 13, '526760')

cols = [
    (.62, '01', 'INSTRUCTION', 'Tìm gì?', 'Xác định tiêu đề thuộc cấu trúc tài liệu ở mọi cấp; phân biệt với nội dung thông thường và mục lục.'),
    (4.88, '02', 'SYSTEM PROMPT', 'Làm việc thế nào?', 'Hướng dẫn AI đọc văn bản, định dạng và ngữ cảnh; dùng công cụ khi cần và trả kết quả theo mẫu.'),
    (9.05, '03', 'PROMPT GOM NHÓM', 'Ghép thế nào?', 'Xem các dòng đã chọn, xác định những dòng cùng tạo thành một tiêu đề và giữ đúng thứ tự nguồn.')
]
for x, n, title, question, body in cols:
    text(x, 1.83, .62, .35, n, 18, '7354A3', True)
    text(x, 2.32, 3.68, .32, title, 12, '7354A3', True)
    text(x, 2.78, 3.70, .42, question, 20, '132F2C', True)
    rect(x, 3.36, 3.42, .025, 'CAD9D3')
    text(x, 3.62, 3.55, 1.30, body, 15, '334D46')

rect(.62, 5.52, 12.05, .65, 'E4F1EC')
text(.86, 5.67, 11.58, .28, 'Đọc bằng chứng     →     Chọn dòng tiêu đề     →     Gom thành tiêu đề hoàn chỉnh', 15, '136B5B', True, PP_ALIGN.CENTER)
text(.52, 7.13, 12.2, .18, 'PRODUCTION EXTRACTION  /  HƯỚNG DẪN AI', 8, '60716C')
slide.notes_slide.notes_text_frame.text = 'Instruction nêu mục tiêu và tiêu chí chọn heading. System prompt hướng dẫn cách dùng dữ liệu, công cụ và contract trả lời. Prompt GROUP chia các dòng đã chọn thành các nhóm tiêu đề; grouping không có quyền tự quyết lại membership.'
prs.save(output)
print(output, 'slides:', len(prs.slides))

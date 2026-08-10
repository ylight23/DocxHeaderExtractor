Heading corpus converted to Word DOCX.
Total document files processed: 95
converted_doc_text: 2
converted_pdf_text_layout: 81
copied_existing_docx: 10
warning_empty_pdf_text: 2

Method:
- Existing .docx files were copied unchanged.
- PDF files were converted to text-layout DOCX using pdftotext -layout.
- Legacy .doc files were converted to text DOCX using antiword.

Limitations:
- This is text/layout conversion, not perfect visual reconstruction.
- Semantic Heading 1/2/3 styles are not guaranteed; useful for extraction corpus testing.
- warning_empty_pdf_text means the PDF had no extractable text layer in this run.

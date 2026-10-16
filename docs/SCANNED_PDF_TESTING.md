# Scanned-PDF petition processing

Delivery: 16 October 2026.

## Use

Open New complaint, choose document input, select a PDF and confirm processing consent. Choose Extract petition text. Compare the editable transcript with every source page, correct errors, prepare the draft and review its routing before registration. Extraction alone does not create a case. Intake PDFs are not retained as evidence attachments.

Text-only PDFs stay local. A PDF containing an image or a page without readable text is conservatively routed to the existing configured OCR service in full. This includes mixed documents and text documents containing image logos. The configured model must support PDF file input and JSON output. No additional API account is required, but OCR does require network access and the existing configured service; it is not offline OCR.

## Bounds and failure behavior

Files are limited to 8 MiB, PDFs to 10 pages and the resulting transcript (including page labels) to 6,000 characters. Local inspection remains in a separate process with a 15-second deadline and 256 MiB working-set watchdog. Encrypted or malformed PDFs are rejected. Provider requests use the existing bounded response handling and timeout.

Provider output must contain exactly one entry per page in order and finish normally. Missing/duplicate/out-of-order pages, malformed content, token-truncated responses and oversized transcripts are rejected. These structural checks cannot prove every visible word was transcribed accurately. Every page still needs human review, particularly Kannada handwriting and faint scans.

Gmail attachments and local case briefings remain local-text extraction only. A mixed/scanned PDF is rejected there rather than silently retaining only its text layer. Download the petition from its source and use the document intake workflow for consented OCR.

## Test scope

Synthetic PDF/image fixtures and provider replies verify local-versus-provider selection, full PDF request transport, page ordering, consent, configuration failure, malformed/oversized input, cancellation and rejected incomplete output. The HTTP flow checks upload, editable extraction without a case, draft creation and reviewed registration. Existing regression tests cover the later officer, contractor, reviewer and citizen lifecycle.

These tests do not establish live provider PDF compatibility, OCR accuracy, handwriting quality or production security acceptance.

## API reference

PDF input uses the documented chat file content part: [OpenAI file inputs](https://developers.openai.com/api/docs/guides/file-inputs). Existing image and voice request formats are unchanged.

## Result

Full regression passed: 353 .NET tests and 67 browser-script tests (420 total), zero failures or skipped tests. The scanned-PDF suite includes 23 new cases.

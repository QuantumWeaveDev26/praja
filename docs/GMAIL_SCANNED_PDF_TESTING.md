# Automatic Gmail scanned PDF intake

Delivered 16 October 2026.

## Use

Keep Gmail connected and automatic intake enabled. Send a complaint with a scanned or mixed PDF attachment to the connected mailbox. Wait for collection or choose Run now on Automation. Praja extracts all PDF pages, detects likely complaints, and prepares the complaint for officer review and registration. No separate upload is needed. For previously skipped messages, choose Retry skipped emails and then Run now.

Review the OCR transcript against the original Gmail attachment before registration. Source references and page markers remain with the complaint. Gmail is read-only; attachment binaries are not retained as case evidence. Manual labelled-email preview stays local and does not use OCR.

## Limits and data handling

Up to three attachments, 8 MiB aggregate attachment bytes, 64 KiB per text attachment, 10 pages per PDF, and 6,000 combined characters including body and subject. Gmail message/attachment JSON is bounded to 12 MiB; other Gmail responses retain the 128,000-byte bound. Unsupported attachments reject the whole email rather than retain partial evidence. Invalid/encrypted/over-limit PDFs go to manual review.

Text PDFs are extracted locally. Scanned/mixed PDFs are sent in full to the existing configured PDF-capable AI service before local complaint detection. This can include unrelated PDF content. Automation permission and page guidance disclose that behavior. Provider failures and incomplete OCR responses stay unseen and retry later; other supported messages can still proceed. New messages do not require a Gmail label.

## Validation

Synthetic Gmail responses and image/mixed PDF fixtures exercise automatic collection, complete page extraction, complaint preparation, reviewed registration, source-ID deduplication and no Gmail writes. Failure cases cover unavailable provider, missing pages, truncated responses, disabled automation, local-only preview, PDFs larger than the former 64 KiB limit, oversized PDFs, malformed PDFs, 11-page PDFs, failure isolation across messages, and retrying skipped messages without reimporting other mail.

These tests validate application behavior with synthetic provider replies. They do not measure live OCR accuracy, Kannada handwriting quality, or production readiness. Existing lifecycle tests cover downstream officer, contractor, reviewer, citizen and community workflows.

## Result

Full regression: 365 .NET tests and 67 browser-script tests passed (432 total), zero failures or skips. Includes 12 new Gmail scanned-PDF cases.

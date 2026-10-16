# Offline media capture and sync

Delivery: 16 October 2026.

## Behavior

The public offline page saves text, one JPEG/PNG incident photo and one voice recording in IndexedDB. Recording is limited to two minutes. File size is limited to 8 MiB each; the queue holds at most 20 complaints and 50 MiB of media. Recording does not contact a provider while offline.

When reachable, the open page sends saved audio to the existing speech endpoint with consent and a fresh CSRF token. The citizen reviews the transcript before registration. Typed notes remain visible during review. Text and photo registration uses the existing public complaint service, encrypted incident evidence and stable submission key. A failed request or lost response retains media for retry; only a confirmed receipt removes local media.

## Automated coverage

- Queue recreation and reconnect preserve media and the submission key.
- Voice transcription creates no case until transcript review and registration.
- Lost responses, provider errors, throttling and malformed replies retain data.
- Storage/quota failures do not falsely report a receipt or discard media.
- Consent, per-file/aggregate size and record-count limits are checked.
- Microphone denial, recorder errors, oversized recording, two-minute stop and page exit release controls/tracks.
- Multipart photo registration is atomic; invalid content leaves no case, receipt, evidence or notification.
- Repeated registration creates one case and one incident photo.
- The lifecycle test follows offline incident-photo registration through officer assignment, field progress, completion evidence, reviewer verification and citizen approval.
- Public endpoints continue to require CSRF and do not grant officer access.

## Practical limits

Automated browser tests simulate recording and device storage; they are not a real-phone microphone/storage-eviction trial. Speech tests use synthetic provider responses. Voice transcription requires the already configured speech service when connected. Synchronisation runs only while the page is open. Offline GPS, audio as permanent case evidence and background sync after closing the page are not included.

Existing users may need to reopen or refresh the offline page after its service worker updates. Local storage is private only to the browser profile, can be cleared by the browser/user, and is not a backup. Save the queued complaint before closing the page.

## Result

Full regression passed: 330 .NET tests and 67 browser-script tests (397 total), with zero failures or skipped tests.

# Praja delivery status

Updated 16 October 2026. Praja is a local prototype, not an approved government
integration or production acceptance certificate.

## Implemented

- Citizen text and voice intake, incident photos and permission-based location
  capture; private tracking codes and citizen approval or dispute.
- Offline text, incident photo and voice capture with bounded device storage, later synchronisation and duplicate protection. Voice is transcribed after reconnecting and requires citizen review before registration.
- AI-assisted extraction and drafting, local routing fallback, PDF text extraction
  and image/audio connectors. Accuracy needs representative evaluation.
- Gmail Inbox collection, complaint detection and prepared drafts; manual/CSV
  intake and explicitly fictional social demonstrations.
- Gmail plain-text and HTML bodies; HTML becomes plain text without rendering or
  remote-resource loading. Up to three UTF-8 text or text-PDF attachments are
  supported, each at most 64 KiB, within a 6,000-character combined extraction limit.
  Unsupported parts send the whole message to manual review; transient failures
  remain retryable. Attachment binaries are not retained as case evidence.
- Admin, intake, department, reviewer and contractor roles with scoped dashboards;
  account management, reassignment, cross-department transfer and mandatory
  first-login password changes.
- Contractor progress and completion evidence, officer review, independent reviewer
  verification and citizen approval or return for further work.
- In-app notifications, assigned-case deadline escalation, and first-assignment
  reminders at 24 hours, escalation at three days and urgent escalation at seven days.
- Opt-in public complaint map and dated public stage history. Private text,
  identities, photos and exact coordinates remain excluded.
- Nearby community feedback, officer investigation/reopening and fresh feedback
  after a later repair round, preserving earlier reports. Community feedback uses
  browser identity and is not verified identity voting or proof of physical repair.

## Next work without new external credentials

1. Representative complaint-detection evaluation and clearer handling of uncertain
   or missed intake messages.
2. Real-device offline media, microphone and storage eviction testing across target phones.
3. Scanned-PDF processing and multilingual/media evaluation tooling and fixtures.
4. Recurrence/department-performance analytics and duplicate/paraphrase review.
5. Backup/restore drills, retention controls, accessibility, real-device testing,
   load/failure testing and security review.

Representative Kannada speech/handwriting and repair-site checks require human
evaluation. Production validation also needs the eventual hosting environment and
approved operating policy.

## External access or decisions needed

- SMS provider for mobile OTP, account recovery and alerts. Citizen identity-proof
  registration/verification needs a separate approved workflow.
- X, Facebook and Instagram developer access and supported collection adapters.
  Social fixtures do not perform live monitoring.
- Email/SMS notification delivery setup; current notifications are in-app.
- Government sandbox/API contracts and authoritative department, location and
  service mappings for official integration.
- Production hosting, HTTPS, database deployment validation, managed secrets and
  protection keys, approved data processing and independent acceptance review.

## Verification

Run `./test.ps1` for the current .NET integration/unit and browser-script results.
Tests use isolated databases and synthetic provider responses. Passing automated
tests does not establish real-device microphone/GPS accuracy, accessibility,
production security or government acceptance. Live credentials and local case data
are intentionally excluded from this repository.

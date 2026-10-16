# Praja grievance workspace

For current scope and remaining work, read [the current delivery status](docs/STATUS.md). Earlier sections and local test reports are dated snapshots; the current status supersedes their pending-work lists. See [source-control guidance](docs/SOURCE_CONTROL.md) for repository maintenance.

A local prototype of the Karnataka AI-enabled grievance-management use case. It provides a real, persisted officer workflow: text intake, draft review, human-confirmed routing, casework, resolution checklist, officer-approved closure and audit history.

## Run

From PowerShell in this folder:

```powershell
.\run.ps1
```

Open http://127.0.0.1:5080. Create your officer account on the first-time setup screen. Optionally load six clearly marked fictional cases. The setup route becomes unavailable after an account is created. Choose your own password; no default credentials are shipped.

The project-local .NET 10 SDK is installed in `.tools/dotnet`. On a fresh checkout, install a supported .NET 10 SDK from [Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). `global.json` selects the 10.0.401 feature band with roll-forward enabled. Dependencies and development state are kept under `.local`. `App_Data` contains the local database and session protection keys. These folders are excluded from Git.

Stop the app with Ctrl+C in its terminal. Restarting preserves saved cases. To use a different local port, run `.\run.ps1 -Port 5081`.

## Test

```powershell
.\test.ps1
```

Tests exercise English/Kannada extraction and routing, ambiguous routing, guarded closure, authentication, CSRF protection, one-time setup and the complete persisted complaint-to-closure workflow. Integration tests use isolated SQLite databases and synthetic provider replies. SQL Server connectivity and IPGRS services have not been exercised; live AI checks are separate from the automated suite.

The test script also runs JavaScript intake/recorder tests with Node.js. These simulate browser media APIs; they do not replace a microphone test on the target device. Test reports are dated snapshots; run the script for the current verification result.

Voice intake: select Record voice, stop recording, check consent, then select Transcribe audio. The transcript appears without reloading the page. Missing consent and provider errors keep audio available for retry; transcription temporarily locks complaint editing to prevent overwriting edits. Clear audio lets you discard a pending recording before switching intake methods. Refresh the intake page after updates to load the current scripts.

## Implemented behavior

- Admin, intake officer, department officer, reviewer and contractor accounts, authenticated sessions, scoped dashboards and department-aware permissions.
- Kannada/English text intake with explicit consent; location and district capture.
- Separate immutable original statement and editable formal draft.
- AI intake suggestions for district, locality, timeline, requested help and pilot service; unsupported fact values are removed unless they occur in the original text. Local keyword routing is the explicit fallback. All routes require human confirmation.
- Lifecycle: Draft -> Registered -> InProgress -> ClosureReview -> Closed.
- Closure requires a detailed report, evidence reference, recorded positive citizen feedback, and explicit officer approval. The checklist does not authenticate evidence or prove physical resolution.
- Audit entries persisted with each case update; optimistic concurrency rejects stale forms.
- Search/status/service filtering, local dashboard and transparent connection/coverage screen.
- Read-only authenticated `GET /api/cases` and `GET /api/cases/{guid}` contracts. The listing returns at most 200 records. There is no IPGRS submission endpoint yet.

This is a local multi-role prototype with officer account management, reassignment, department transfer and first-login password changes. Citizen tracking uses private codes; real mobile OTP, identity verification and production SSO remain pending. Evidence references can cite retained supporting files or authorised external records. Intake extraction files are not automatically retained; evidence uploads are a separate, consented action. Times are explicitly displayed as UTC.

## Intake inbox

Open **Intake inbox** (`/Intake`) to retain authorised incoming text, reviewed transcripts or social-post references before they become cases. **Capture a message** records the preserved original, channel, language, optional HTTPS source link/message ID, received time and capturing officer. This is officer-entered staging: it does not crawl platforms, fetch source URLs or send replies. Source IDs are scoped to the officer, channel and source host. Social IDs require a source link. Repeat capture returns the first item; changed wording under that ID is rejected.

The review screen flags matching normalised wording in other active inbox items and the latest 200 owner-scoped local cases. It does not recognise paraphrases or automatically merge/discard complaints. An officer can dismiss a pending message with a retained review reason, or continue to detail extraction and draft preparation. Inbox source text and channel stay fixed; the formal draft remains editable. Conversion inserts the case and updates the inbox state in one transaction, with version checks. A processed item cannot silently create a second draft. Prepared cases link back to their source and remain Draft until registration is confirmed.

SQLite adds `InboxMessages` through a tested additive migration. Startup backs up an existing database before applying it. Existing account, case, audit and evidence rows are preserved. A rollback requires retaining any newer data before a reviewed restore; do not drop a populated inbox. Existing SQL Server databases need a separately reviewed migration before this build is deployed there; `EnsureCreated` is not an upgrade mechanism.

For a concise earlier manager overview, use `output/pdf/Praja_Manager_Brief_2026-10-13.pdf`. The newer project status report records current scope and remaining delivery work. Earlier test counts in reports are dated snapshots.

## AI drafting and settings

On **Register a grievance**, enter the original complaint or review extracted document/audio text, check consent, then select **Suggest details**. This works before district and locality are filled. AI suggestions populate only empty location fields; existing officer entries remain intact. The review panel shows reported details, the suggested pilot service and missing information. Correct the location fields, then select **Prepare draft**. The next screen still requires officer confirmation of the formal draft and service before local registration. Direct manual intake remains available.

Suggest details creates no database record. The protected review receipt is bound to the officer and original statement and expires after 30 minutes. If the complaint changes, run suggestions again or select **Clear suggestions** to continue manually. Timeline and requested-help excerpts remain represented in the preserved original statement rather than new database fields. Relative dates are not converted into invented calendar dates. Draft language remains the officer's choice; detected source language is shown separately. Do not treat model suggestions as verified facts or calibrated confidence scores. The taxonomy has 10 demo services, 12 categories and 23 issues plus manual review, not official IPGRS routing codes.

Suggest details and Prepare draft make separate provider requests when AI is configured and may incur charges. Missing consent stops processing before a provider request. Provider failures fall back to keyword routing without extracting location details; missing fields must then be entered manually. The shared provider transport caps replies at 100 KB, enforces a bounded deadline and rejects redirects. Extracted quotes prove text provenance, not their relevance or truth; an officer must review them.

Forgot your local officer password? Open PowerShell and run `& 'C:\Gov Proj\reset-password.ps1'`. Enter and confirm a new password of 14–128 characters; both prompts hide input. Recovery backs up the existing SQLite database under `App_Data/backups`, changes only the single officer's password and lockout, and prints the account email for sign-in. No password is placed in arguments, environment variables, files or chat. Old sessions are invalidated. This is a local filesystem-owner recovery command, not a public web reset endpoint; it is disabled outside Development/Testing and for SQL Server. Production recovery needs an approved identity-management process.

For this local Windows pilot, sign in and open **Connections → Configure OpenAI** (`/AiSettings`). Enter the API key directly in its password field, select model IDs available to your OpenAI project, and optionally enable transcription. Blank keys retain the current credential. Settings are stored as encrypted application-data-protection payloads under `App_Data`, scoped to the current database; Windows protection keys use DPAPI. The credential is never returned in page responses. These local settings override AI environment configuration in Development/Testing only. Production uses managed deployment secrets instead; the local settings page is unavailable there or to non-loopback requests.

**Test drafting** sends only a synthetic water-supply complaint to OpenAI, creates no case, and may incur API charges. It confirms drafting only, not OCR, speech or Kannada accuracy. **Disable AI** removes the stored credential and overrides environment configuration with a disabled local setting. Preserve the protected key store when backing up the workspace; removing its keys can make credentials, sessions and citizen access codes unreadable.

Default local model IDs are `gpt-4o-mini` for text/image chat and `gpt-4o-mini-transcribe` for speech. Availability depends on the API project. The connector uses [OpenAI chat completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create) with JSON output and [audio transcriptions](https://developers.openai.com/api/reference/resources/audio/subresources/transcriptions/methods/create). Drafting/image requests have output-token caps in addition to existing streaming and time limits.

Without credentials, the app uses a source-preserving template and a source excerpt. It does **not** claim translation, model-generated routing, or AI verification. Routing and closure checks remain local rules even when AI drafting is configured.

An approved service exposing the OpenAI-compatible chat-completions JSON contract can be configured through deployment environment variables:

```text
Ai__Endpoint = full HTTPS chat-completions endpoint
Ai__Model = approved model identifier
Ai__ApiKey = secret supplied by your secret manager
```

Never commit API keys. The app sends the original complaint and supplied location to this service. Its response must contain a bounded JSON `title`, `draft`, and `summary`; all output is shown as a draft for human review. Failed or malformed responses fall back to the original-preserving template and are labelled accordingly. Provider redirects are disabled; provider output and citizen text are not logged. A 100 KB streamed response limit and a 30-second end-to-end deadline bound provider requests. `Ai__TimeoutSeconds` may adjust that deadline within 1–60 seconds. The connection screen reports configuration, not a verified service connection. A live synthetic drafting test succeeded on 13 October 2026; OCR, speech and language accuracy still need separate evaluation.

## SQL Server and production deployment

SQLite is used only for the runnable local preview because no SQL Server instance is currently running on this machine. SQL Server is supported through EF Core's SQL Server provider. Set `Database__Provider=SqlServer` and supply `ConnectionStrings__Cases` through secrets. Use an encrypted connection with certificate validation. The SQLite and SQL Server databases are separate; changing providers does not migrate existing data.

SQLite now uses EF migrations. Startup adopts the previous three-table schema only after matching its table/index definitions and checking foreign keys, creates a consistent backup, records the legacy baseline, then applies the additive evidence migration. It refuses unknown legacy schemas instead of recreating them. Repeated startup does not repeat the upgrade or overwrite records. Backups are under `App_Data/backups` unless `Database__BackupDirectory` overrides the directory. If the upgrade fails, stop the app and restore the identified pre-upgrade backup; do not run destructive downgrade commands or delete the database. The additional evidence table can remain while older application code ignores it.

Migration files and the design factory are **SQLite-specific**. The design factory uses `praja-design-only.db` instead of the live workspace. Do not apply these migrations to SQL Server. SQL Server still has the previous fresh-prototype `EnsureCreated` path and is untested; reviewed provider-specific migrations, actual connectivity, backup/restore and deployment checks remain necessary before a SQL Server pilot. Configuration is not proof of tested integration.

Outside Development/Testing, local web setup is disabled. Provision the initial account through `Bootstrap__Email` and `Bootstrap__Password` deployment secrets, then remove those bootstrap secrets after successful provisioning. The app requires SQL Server in that environment, enables HTTPS redirection/HSTS, and uses secure session cookies. Configure `AllowedHosts` for the approved hostname. Windows session keys use DPAPI; Linux deployments require an appropriate protected shared key store. Reverse-proxy configuration, service identity, secret management and multi-instance key handling need deployment-specific review.

Local safeguards include server-side validation, parameterised EF queries, CSRF-protected mutations, request-rate limits on login, account lockout, CSP, no external scripts/fonts, no framing, private-record scoping, and generic production error responses. The application has **not** undergone an independent security audit and does not establish TRL 7 readiness.

## IPGRS access needed

No government login, database or portal is accessed by this app. Local PRJ/DEMO references are not official grievance IDs. Obtain these inputs before building the live connector:

1. Approved API specification and sandbox credentials.
2. Authoritative department/category/subcategory and location codes.
3. Authentication, network/allowlist, error and rate-limit requirements.
4. Registration, status, attachment and closure contracts.
5. Idempotency/retry rules and reconciliation behavior.

## Subsequent use cases

Social-media integration, live IPGRS integration and government-wide policy analytics remain unimplemented. Voice/OCR provider connectors are implemented but need approved credentials and real Kannada evaluation. The Connections page maps all nine use cases to the current behavior and remaining work.

## Research used for the initial technology choice

- [NeGD's Karnataka iPGRS listing](https://negd.gov.in/isl/Directory/statedata/407): published ASP.NET Framework / MS-SQL / Chart.js / SSRS / on-premise architecture; current versions still require departmental confirmation.
- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).
- [GIGW government website/app guidance](https://guidelines.india.gov.in/guidelines/): security audit, access controls, hosting, backups and accessibility guidance.
- [API Setu integration guidance](https://docs.apisetu.gov.in/document-central/explore-apisetu/Overview.html): documented API access and publisher approval. This does not establish that IPGRS is available through API Setu.

No official government branding or approval is claimed.


## Document and voice intake

The Register a grievance screen accepts UTF-8 text, text/scanned/mixed PDFs and PNG/JPEG petitions. Text-only PDF extraction needs no external AI. PDFs are parsed in a separate process with a 15-second deadline and a 256 MB working-set watchdog. Uploads have an 8 MB limit; PDFs have a 10-page limit; the original complaint retains its existing 6,000-character limit. Over-limit or unreadable content is rejected rather than silently truncated. These limits and process isolation reduce exposure; they are not a substitute for a production upload/security audit.

For images, `Ai__Endpoint`, `Ai__Model` and `Ai__ApiKey` must identify an approved vision-capable chat-completions service. The image is sent as a data URI for transcription. Handwriting accuracy has not been evaluated. Scanned and mixed PDFs are inspected locally, then sent in full to the configured service after consent. The model must also support PDF file input. Every page must be returned in order; incomplete or token-truncated responses are rejected. Numbered page transcripts remain editable before draft review and registration. This is provider OCR, not offline OCR; Kannada handwriting accuracy still needs representative evaluation. Gmail and local case-briefing PDF extraction remain text-only and reject image/blank-page PDFs rather than silently omit content.

Voice recording is initiated by the user's Record button and browser microphone permission, stops after two minutes, and can be played locally. Supported transcription uploads are WAV, MP3, WebM and Ogg. Configure these additional environment variables for an approved OpenAI-compatible speech endpoint:

```text
Ai__TranscriptionEndpoint = full approved HTTPS audio-transcriptions endpoint
Ai__TranscriptionModel = approved speech model identifier
Ai__ApiKey = secret supplied by your secret manager
```

File processing requires consent. The image/audio bytes are transmitted only when the corresponding processing action is submitted. Provider processing is limited to a 30-second deadline and 100 KB response. Files themselves are not stored with the case. Always compare extracted text/transcripts with the original before preparing the draft. Extraction alone does not register or save a complaint. Microphone recording and live external OCR/speech have not been exercised automatically; local text/PDF extraction and mocked provider contracts are tested.

## Citizen assistance and private status

`/Citizen` is public local guidance in English/Kannada. It uses defined questions and keyword rules; it is not an AI chatbot and does not create government complaints. An authenticated officer can issue a private seven-day access code from a registered case's detail page. Issuance is audited without storing the code. A citizen enters it through a CSRF-protected POST form to view only reference, title, service, district, current status and update time. Original narratives, drafts, location details, evidence and audit history are not exposed. A reference alone cannot retrieve a case.

Codes are bearer credentials: anyone holding a valid code can see that limited summary. They are encrypted/authenticated using application data protection, expire at seven days, and are rejected if tampered, expired, associated with another owner, or the case is back in Draft. Codes are not accepted from a URL and are not echoed into the tracking response. Issuing another code does not revoke earlier ones; all expire independently. Individual revocation requires future persistent token management before a wider pilot.

## Analytics

`/Analytics` is authenticated and owner-scoped. Filter by last 7/30/90 UTC calendar days or all time, district and service. Fictional samples are excluded unless explicitly included. Reports show local volumes/current statuses, recorded creation-to-closure duration, department/district/source groupings, daily intake and outstanding document-checklist flags. Missing, negative or future closure times are excluded from duration calculations. There are no fabricated targets, SLAs or government-wide performance claims.

Filtered CSV downloads include only reference, title, district, service, status, UTC timestamps and sample flag. Complaint narrative, location and citizen feedback are omitted. Cells are quoted and leading spreadsheet formula operators are neutralised. Treat exports as private workspace data.

Stop the running app before rebuilding or running the test script on Windows, because the application executable may be locked while serving the preview. Existing records are preserved by the guarded, backed-up SQLite upgrade.

## Supporting evidence files

Registered, in-progress and closure-review cases can retain PDF, PNG and JPEG files through the case details screen. Confirm permission, choose a file of at most 8 MB and optionally describe its relevance. Each case allows 10 files; duplicate content is rejected by SHA-256. Closed cases remain readable but cannot accept new files. No delete/replacement operation is provided in this version.

Bytes are encrypted with application data protection and stored in the same database as the case. Upload, case-version increment and audit entry commit together; concurrent case updates roll back the upload. Filenames are stripped of paths and encoded by Razor. Downloads require the handling officer's session, verify ownership and file integrity, and use attachment/octet-stream responses with no inline preview. Citizen status and existing JSON case APIs do not expose files. Supporting files are not sent to the AI provider.

A file signature check identifies the allowed formats; it is not malware scanning or authenticity verification. The officer must inspect evidence and cite a file ID or an authorised external record in the closure review. A retained attachment alone does not satisfy the existing closure checklist. Production attachment malware scanning, retention/deletion workflows and operational evaluation remain pending. Multipart buffering can use temporary local files before retained bytes are encrypted; protect the service's temporary directory in deployment.

Database backups now include encrypted evidence bytes. Keep protection keys with private backups. Evidence protection is bound to the database identity: restore to the original database location, or plan an authorised decrypt/re-encrypt migration when changing database identity or Windows protection context.

## Live provider verification record

On 13 October 2026 the user saved OpenAI settings and confirmed a successful synthetic drafting test in the app. OCR, speech and Kannada accuracy still require separate checks. The first attempt failed because the agent-launched preview process had restricted socket access; relaunching the local preview with approved network access allowed the drafting request to succeed. If a future preview shows the same socket error, correct its launch permissions rather than changing the API key or weakening the application's protections.

## Prototype usability review — 14 October 2026

Government integration is a future stage and does not block local demonstration. The home screen offers direct capture, intake review and citizen-status tasks. **New complaint** now has text, voice and document choices with consent above the tools. Pending media must be processed or deliberately cleared before switching methods or preparing a draft; location-field Enter prepares a draft and cannot repeat a pending submission. Existing server intake and inbox forms remain compatible.

Research against Karnataka iPGRS, CPGRAMS/Samadhan Didi, FixMyStreet and SeeClickFix, current limitations, demo instructions and verification evidence are recorded in [the comparison and usability report](output/Praja_Comparison_and_UX_Review_2026-10-14.txt). AI voice intake and automatic categorisation already exist in comparable systems; no uniqueness, official approval or comparative accuracy claim is made. The local prototype can demonstrate its own source preservation, reviewed suggestions, case progress, private evidence, closure checklist, citizen status and analytics.

### Guided citizen follow-up

After suggesting details, use **Need more details? Ask the citizen** to record an optional timeline (120 characters) and requested help (500 characters). Unknown answers stay blank. Confirm supplied answers before preparing the draft; they are added as a separate labelled section with an audit entry, preserving the original and any inbox source. Kannada answers remain verbatim, with Kannada section labels when that draft language is chosen. These two answers are not sent to AI; the original, district and locality still reach configured AI drafting. They do not automatically update the suggested route or source-only summary.

Changing an answer or the original clears browser confirmation. Native validation reopens a collapsed follow-up section, and server validation also rejects unconfirmed or oversized answers. Existing forms that omit follow-up fields remain supported. No schema migration was needed. This is a guided officer form, not an autonomous chatbot. See [the follow-up update and test record](output/Praja_FollowUp_Update_2026-10-14.txt).

### Demo categories and routing

The routing catalog now has 10 services, 12 categories and 23 issues, plus manual review. New health, school, transport and agriculture labels are demonstration choices, not official government routing codes. Suggest details displays the classification, method and a source-supported excerpt. Configured AI uses validated taxonomy keys; absent category fields use explicitly labelled local issue rules. Local English/Kannada keyword matching is conservative for mixed services and does not choose an arbitrary issue when several match.

Draft review offers dependent category/issue selectors. Officers can leave them unclassified, clear an initial suggestion or correct the route. Server checks reject mismatched parents. Recoverable validation errors retain draft edits and require fresh review; version conflicts refresh current saved state. Final choices are recorded with an audit entry. Legacy cases are not silently reclassified.

SQLite migration `AddDemoRouting` adds four empty-default metadata columns and uses the existing pre-upgrade backup path. Existing records were verified unchanged. Rollback requires the pre-upgrade backup, matching earlier application and protection keys; earlier binaries reject the new migration history. SQL Server migration/validation remains separate. See [the routing update, live checks and preservation report](output/Praja_Routing_Update_2026-10-14.txt).

### Reviewed channel imports

Open **Intake inbox → Import messages**. Load three fictional messages, or paste a CSV batch using columns `channel,external_id,language,source_url,message`. Supported import channels are Email, Field meeting and Social media; language is English or Kannada. Each message requires a stable source ID and 20–6,000 characters. Social posts require an HTTPS reference without embedded credentials. Source links are never fetched. Quote text containing commas, double quotes or line breaks. A batch allows 20 messages and 32,768 CSV characters.

**Preview messages** validates the whole batch and displays source wording, local service/category/issue hints, existing source IDs and matching wording. It writes no inbox records and calls no AI provider. **Edit messages** restores the batch for correction and requires another preview. Confirm retention permission before **Confirm import**. A protected owner-bound preview expires after 30 minutes; confirmation uses that exact payload, not a replacement posted CSV. Import commits the complete batch together or rolls it back. Repeated source IDs reuse the first saved message and preserve its state; different normalised wording with the same ID rejects the whole batch. Different IDs with matching wording remain separate records with review flags.

After import, open a message and choose **Review details and prepare draft**. The source channel and original remain intact. Use the existing consent, suggestion, draft review and registration flow. Exact built-in fictional fixtures become Sample cases and remain excluded from normal analytics by default. No mailbox connector, social crawler, automatic complaint registration or government submission was added. No schema migration was required. See [the channel-import update and test record](output/Praja_Channel_Import_Update_2026-10-14.txt).

### Source-linked case briefing

Open a case and select **Prepare briefing**. Generate local excerpts from the original, recorded resolution, citizen feedback and latest 30 activity entries, or choose consented AI excerpt selection. Include up to two supporting documents explicitly: text PDFs use the existing isolated local parser; selected images require AI OCR and must be compared with their originals. Unreadable files are labelled as excluded. No file content is processed just by opening the page.

AI points require valid source IDs and exact contiguous quotations. Unsupported output or provider failure uses local excerpts. Recorded citizen feedback, including disagreement, always appears separately. Source links open the complete considered text. The briefing is a temporary page result, not a saved case update or an independent resolution decision; a version change during preparation invalidates it. AI processing can transmit the case text, recent activity and selected document text/images only after the officer confirms permission. Without AI, local briefing and text-PDF extraction require no external request.

### Gmail complaint connection

**Connections → Configure Gmail** (`/GmailSettings`) now accepts your Google Web application client ID and secret. Register `http://127.0.0.1:5080/Connections/GmailCallback` in Google for the main local demo; the configuration page displays the URI matching its current host and port. JavaScript origins are not used. Save credentials, confirm connection permission and select **Connect Gmail**. The user chooses the configured mailbox in Google and grants read-only Gmail access; Praja verifies the returned account. Credentials/tokens are encrypted for the officer and workspace and are never displayed.

Create a custom Gmail label **Praja-Complaints** and apply it only to complaint emails. **Preview Gmail messages** resolves that exact custom label, considers up to five matching emails and displays source text plus local routing hints. It rejects built-in labels and checks each returned message's current labels. Plain text is preferred; HTML-only bodies are converted into bounded plain text without rendering HTML or fetching remote resources. Up to three UTF-8 text or text-PDF attachments, each at most 64 KiB, can contribute extracted text within the 6,000-character combined limit. Unsupported or oversized parts route the whole message to manual review instead of silently dropping evidence; transient provider failures remain retryable. Attachment binaries are not retained as case evidence. Confirm retention permission separately to import into the existing review inbox. Fetch/import contacts no AI, registers no case and makes no Gmail changes. Repeated source IDs reuse the first saved record. Disconnect removes saved tokens; retained local messages remain.

OAuth state is owner-bound, expiring and single-use; PKCE and per-attempt generation checks are included. Fixed Google HTTPS endpoints, bounded replies, refresh handling, CSRF and rate limits are reused. The connector has synthetic flow tests; live mailbox connection and basic preview/import have been exercised locally. HTML/attachment regression tests use synthetic provider replies. Background Inbox collection is described below. Sending replies and production OAuth verification remain separate work.

### Public dataset validation

A bounded sample of 20 official NYC 311 records was adapted into marked English test messages and exercised in the separate QA workspace on port 5081. Browser import retained all 20; repeat import added zero duplicates. A live AI draft was reviewed and one test case registered. Originals preserved their wording with browser line-ending normalization, and all source links were retained. Main casework contains none of these dataset records. Structured foreign records are not a Karnataka/Kannada classification benchmark; keyword coverage and AI source-context retention need further evaluation. See [the public dataset test report](output/Praja_Public_Dataset_Test_Report_2026-10-14.txt), [the reusable CSV](output/Praja_Public_Dataset_Batch_2026-10-14.csv), and [the fictional Gmail test email](output/Praja_Gmail_Test_Email_2026-10-14.txt).

### Automatic collection and preparation

Open **Automation** (`/Automation`). With one-time mailbox processing permission, the local worker checks the connected Gmail Inbox every 60 seconds, applies local complaint-detection rules, retains likely complaints, and prepares source-supported details, routing and drafts. This path does not require Gmail labels. A run considers up to 100 Inbox IDs, reads up to 20 unseen supported emails and prepares up to five pending messages. HTML bodies and supported text/PDF attachments use the same bounded extraction described above; unsupported whole messages require manual review, and transient failures can retry. Detection can miss complaints or flag unrelated messages; no accuracy score is claimed. The worker stops when the app stops.

Prepared complaints are kept in an encrypted owner/workspace-bound queue. **Review prepared complaint** opens populated fields and the preserved original; correct exceptions, then **Approve and register**. Missing district/location or uncertain routing requires a check. Approval reuses the prepared draft and atomically registers a single local case; it does not call AI again or automatically close cases. Permission is bound to the configured mailbox. **Pause automation** preserves work and stops subsequent processing steps.

The **social feed demo** prepares three explicitly fictional X/Facebook/Instagram posts through that same queue. No social platform is contacted; live collection and reply posting still need separate integration work and platform access. Repeated demo runs reuse source IDs, and approved demo cases are marked Sample. See [the automation implementation and verification report](output/Praja_Automation_Update_2026-10-15.txt).


## Offline photo and voice capture

Open `/offline.html` once while connected, then use the same browser on your own device. Enter the district and locality, type a complaint or record up to two minutes of audio, optionally choose an incident photo, and confirm the relevant permissions. Choose **Save and queue complaint** before closing the page. Unsaved microphone recordings are not protected against closing the tab.

The queue stores media in IndexedDB, with at most 20 records, 8 MiB per file and 50 MiB of combined media. Supported photos are JPEG/PNG; audio is WAV, MP3, WebM or Ogg. Browser storage can be cleared or evicted, so this is not a backup. Local copies are accessible to other people using that browser profile.

Reopen the page after reconnecting. Text/photo complaints sync automatically while the page is open. Voice recordings use the configured speech service after reconnecting; no new provider is needed, but transcription cannot run without a working speech configuration. Review and correct the transcript, then choose **Transcript is correct — send complaint**. Audio is used for transcription, not retained as case evidence. Incident photos are retained through the existing protected evidence service.

A returned reference and private tracking code confirm registration. Failed requests retain the local media and stable submission key for retry. Confirmed receipts remove local photo/audio blobs. Sync does not run with the page closed. Offline GPS capture is not part of this delivery; use the online form for device coordinates.


### Scanned PDF email intake

Automatic Gmail intake processes scanned and mixed PDF attachments with the existing configured PDF-capable AI service. It inspects every page, sends the complete PDF when OCR is needed, includes page-marked text in complaint detection, and prepares likely complaints for officer review. No manual download/upload is required. Text PDFs remain local. Manual labelled-email preview remains local and does not invoke OCR.

Automatic intake accepts up to three attachments, 8 MiB aggregate attachment bytes, 64 KiB per text attachment, 10 pages per PDF and 6,000 combined characters. PDFs may reach AI before complaint detection, including unrelated PDFs. Provider failures remain unseen for retry; malformed/unsupported attachments exclude the whole email. Attachment binaries are not stored as evidence. Existing source IDs prevent duplicate intake. Use **Retry skipped emails** on Automation for previously unsupported messages in the current mailbox, then **Run now**. Compare OCR with the source before registering.


### Missed email complaints

Automation lists the latest 50 references to emails not detected as complaints. Officers can read each original in Gmail, confirm processing permission and select **Recheck as complaint**. **Run now** then fetches the message directly and prepares it for review, bypassing only the local complaint keyword test. Size/content validation, mailbox scope and final registration review still apply. **Dismiss review reference** removes the reminder without changing Gmail. Unrelated email bodies and subjects are not retained in this list. See `docs/MISSED_EMAIL_REVIEW_TESTING.md` for limits and test coverage.


### Duplicate complaint review

Intake inbox now shows exact and similar-wording candidate matches. Officers can link a confirmed duplicate to a registered case or keep it separate with a recorded reason. Original messages remain intact; linked reports appear on case details and under the Duplicate inbox filter. Automation and case pages show related-record hints. No automatic merging or citizen access sharing occurs. Matching is local, bounded and heuristic; see `docs/DUPLICATE_REVIEW_TESTING.md` for workflow, limits and validation.

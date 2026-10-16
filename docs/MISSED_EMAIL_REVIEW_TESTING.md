# Recovering missed email complaints

## Use

Open Automation and find Emails not detected as complaints. The detector uses local keyword rules and may miss indirect wording. Read the original in Gmail. If it is a complaint, check the processing permission and select Recheck as complaint, then Run now (enable automation if paused). The message is fetched again from the connected Inbox and prepared using the existing extraction/drafting pipeline. Review the prepared complaint before registration.

Dismiss review reference removes the local reminder and leaves the message marked as processed. It does not delete or change Gmail mail. Only the latest 50 references across mailboxes are retained. Subjects and bodies of rule-rejected messages are not stored in this list. Earlier rule-rejected messages from before this feature cannot be reconstructed from the old Seen IDs and must use existing manual intake.

## Behavior and limits

Recheck validates the connected mailbox and a listed message reference. It cannot request arbitrary message IDs. Permission is required, requests are protected against CSRF, and existing officer route authorization remains in force. A shared collection lock prevents concurrent recheck/dismiss and collection mutations. Requested IDs are mailbox-scoped, deduplicated, and fetched directly rather than depending on the first 100 Inbox IDs. Up to 20 queued requests run before normal intake; persistent failures can delay normal collection until an officer dismisses those requests.

Gmail labels, original source references and all MIME/size/PDF bounds still apply. A message that left Inbox or has unsupported content goes to manual review. Provider failures keep the request pending. Successful recovery removes its reminder and override; source-ID deduplication prevents repeated intake. Processing does not register a case automatically.

This adds a recovery workflow, not a trained classifier or a claim of measured detection accuracy. Representative real complaint and non-complaint evaluation remains pending.

## Verification result

Full regression passed: 371 .NET tests and 67 browser-script tests, zero failures or skips. After adding the final unsupported-attachment test, the focused missed-message suite passed all 8 matching tests (7 new recovery cases plus an existing matching test). Tests use isolated databases and synthetic Gmail/provider responses. No real mail was sent or altered.

# Duplicate complaint detection and officer review

## Use

Open an incoming message in Intake inbox. Possible duplicate complaints lists exact normalized matches and similar-wording hints in the latest 200 pending inbox messages and 200 local cases owned by the same workspace account. Each list is capped at ten matches. Automation review and case details also show candidate links.

Compare location, incident dates and original source wording. For the same incident, choose an existing registered case, confirm the duplicate, and record a 20–500 character reason. The message moves to Duplicate and links to that case; no extra case is created. Filter the intake inbox by Duplicate to find it later. Case details list linked reports, preserving each original message, source reference, reviewer, time and reason. Multiple reports can link to one case without altering the original one-to-one conversion link.

For a separate incident, record why and choose Keep separate. The message remains pending with a new version. Prepare/register a new complaint normally; the review reason is copied into its case audit. Automation discards the old prepared version and rebuilds it on the next run. Linking does not change case progress, closure, citizen identity/access or citizen approval.

## Detection limits

Matching is local and needs no API credentials. Exact matching normalizes Unicode, whitespace and casing. Similar wording requires at least five shared content tokens, a Dice overlap score of at least 0.72 and matching numeric tokens. This supports reordered and moderately reworded reports; it is not semantic paraphrase recognition, a geographic match, or a calibrated confidence score. Numeric conflicts exclude fuzzy matches. Kannada tokens are supported without translation, but Kannada accuracy has not been evaluated.

Officers decide; suggestions do not automatically suppress registration. Draft/sample cases cannot receive duplicate links. Existing registered cases are not merged; direct citizen submissions keep their own tracking and workflow. This release adds incoming-message review and related-case hints, not a universal case-merging system. Sample cases are excluded from candidate search. Matches outside the bounded recent search can be missed.

## Data and migration

SQLite adds nullable InboxMessages.DuplicateOfId with a non-unique index and restricted foreign key. Existing source/case relationships remain unchanged. Workspace initialization backs up an existing database before migration. Downgrading removes the duplicate link column; preserve a backup and export review relationships before any production rollback. No production rollback was performed. SQL Server deployment still requires the existing provider-specific migration review.

## Verification

Tests cover normalized and similar wording, conflicting numbers, weak/unrelated matches, owner isolation, multiple source links, confirmation, reasons, stale versions, concurrent changes, source preservation, duplicate conversion prevention, separate-case registration, anonymous access, migration preservation and prepared-draft regeneration. Tests use isolated SQLite databases and synthetic providers. They do not establish real-world detection precision or recall.

## Result

Full regression passed: 396 .NET tests and 67 browser-script tests (463 total), zero failures or skips. Includes 24 new duplicate-review, migration and automation cases.

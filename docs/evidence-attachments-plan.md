# Evidence attachments implementation plan

Goal: let the officer retain and download supporting PDF/PNG/JPEG evidence for registered cases, while preserving existing accounts/cases and human closure review.

Use an additive evidence table with encrypted bytes, metadata and SHA-256. Uploads require consent, ownership and the current case version; reject Draft/Closed cases, duplicates, files over 8 MB and more than 10 attachments per case. Download as attachments, without public/inline previews. Store and audit an upload atomically. A stored file is supporting material, not a verified resolution.

1. Add regression tests for authenticated upload/download and forbidden access; prove absence before implementation.
2. Generate an EF baseline for the original model, then an additive evidence migration. Adopt only a verified legacy SQLite schema after a consistent backup; reject unknown schemas. Fresh databases use migrations. Production migrations remain an explicit deployment step.
3. Implement bounded file validation, encrypted storage, ownership, concurrency and audit. Add upload/download controls to the existing case page.
4. Prove legacy-row preservation, repeat startup, failure rollback, invalid files, denied owners, private citizen status, quota/concurrency and encryption/integrity. Run the full suite and browser checks using synthetic data.

Rollback: restore the pre-upgrade backup with the app stopped if migration verification fails. The additive schema can also remain in place while older application code ignores the evidence table. Do not drop any existing tables or remove evidence to roll back automatically. Keep application protection keys with private backups.

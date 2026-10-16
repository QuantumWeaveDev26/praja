# X public complaint intake

This local connector uses X's official recent-search API. It is on-demand and
requires an X developer app bearer token and available API access/credits.
Facebook and Instagram are not connected by this feature.

## Setup and use

1. Sign in as the workspace admin. Open Connections, then Configure X search
   (`/XSettings`) from this computer.
2. Paste the bearer token directly into the password field. Keep it out of chat,
   source code and screenshots. Review the search query and save.
3. Saving performs no API call. The token is encrypted using local data protection,
   bound to the account and database. Leaving it blank retains the saved token.
4. Check X billing/spending limits in its console. Confirm the query, processing
   permission and potential charges, then choose Search latest posts.
5. Each request asks for at most 10 recent posts. Collect next 10 results explicitly
   continues the returned cursor. No polling, automatic retries or recurring charges
   are enabled by Praja. Repeating latest search may incur costs for existing results.
6. Open Intake inbox and review matching posts. They retain original text and an X
   source link. Review duplicate candidates, dismiss unrelated posts or prepare a
   local draft and confirm routing before registration.
7. Remove saved token disconnects this workspace; retained inbox records remain.
   Revoke the token in X separately if required.

## Scope and limitations

Recent search covers the last seven days, matching query terms rather than verified
incident location. The starter query includes selected English/Kannada locations and
civic terms; it does not cover every Karnataka district or every complaint wording.
Change the query for other districts/issues. Posts without useful locality require
manual verification. A matching post is not automatically a verified grievance.

All supported matching text is staged for review, including potentially unrelated
results. Text outside 20–6,000 characters is skipped and counted. Images, video,
private messages and author profile details are not fetched. Original publication
times are not imported; inbox receipt time means local collection time. No replies
or public publication occur. Only English/Kannada draft choices are supported;
source text remains unchanged even when its detected language differs.

The collector does not invoke AI. Automatic Gmail draft preparation excludes these
X imports. Their intake suggestions and drafts use local rules/templates, and case
briefing permits local text only. External AI processing of X sources is not enabled
in this delivery. Existing manually pasted content cannot be identified as X data
automatically.

Post IDs are owner-scoped. The first edit-history ID prevents duplicate intake of
edits while retaining the first captured wording. This is not a deletion/edit
compliance synchronisation service. Before production use, platform terms, data
retention/deletion obligations and operational access require review.

## Reliability and safeguards

Only the fixed HTTPS X search endpoint is called, using an Authorization header.
Redirects are disabled; replies are limited to 200 KB and operations to 30 seconds.
The complete response is validated before transactional capture. Malformed/partial
responses do not advance pagination. A database commit followed by settings-write
failure can leave already retained posts; retry safely deduplicates them.

Saved query/token changes and successful requests rotate a version. Stale forms
cannot unknowingly consume a newer cursor or use a changed query. A per-owner lock
prevents overlapping collection/settings changes. Raw provider responses and tokens
are not returned in errors or HTTP-client logs. The settings page is admin-only,
loopback-only, local-environment-only, CSRF-protected and rate-limited.

## Validation

Automated tests use synthetic HTTP replies and isolated databases, not the user's
token or paid X API. They cover settings, credentials, access, search, pagination,
source preservation, duplicate edits and failure paths. Run `./test.ps1`.
Live connectivity is unverified until the user saves a token and performs an
explicit successful search. A saved token alone is not proof of access.

References checked 16 October 2026:
- https://docs.x.com/x-api/posts/search/quickstart/recent-search
- https://docs.x.com/x-api/posts/search/introduction
- https://docs.x.com/x-api/getting-started/pricing

Validation on 16 October 2026: all 433 application tests and 67 browser-script tests passed (500 total). No live X request was made.

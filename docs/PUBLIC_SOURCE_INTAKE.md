# YouTube and Karnataka news intake

Open **Connections → YouTube and Karnataka news**, or `/PublicSources`, as the
local workspace admin. The page is restricted to Development/Testing on loopback,
like the X connector. Saving settings makes no external request.

## First collection

1. Save a query containing a place and issue, e.g. `Bengaluru water`, `Mysuru garbage`
   or `ಬೆಂಗಳೂರು ನೀರು`. Try alternate spellings in separate runs.
2. Select The Hindu Karnataka or Citizen Matters, confirm retention and **Collect
   once**. News feeds need no API key. Feed search requires every space-separated
   query word in the retained headline/excerpt/attribution; no stemming or translation.
3. Review results in **Intake inbox**. Open the original source and confirm date,
   actual location and whether an unresolved civic issue exists. Prepare a draft
   using local hints/templates, then review before registration. An article is not
   a direct citizen statement or verified evidence of a current grievance.

## YouTube setup

Enable **YouTube Data API v3** in a Google Cloud project. Create an API key restricted
to that API, enter it only in the password field on `/PublicSources`, and save.
This is a server request, so browser referrer restrictions are unsuitable; IP
restrictions must match the server's public egress IP if configured. Never commit
keys or send them through chat. The key is protected with ASP.NET Data Protection,
scoped to the local database and owner. Remove saved key disconnects future YouTube
requests without deleting previously retained metadata.

Select YouTube and collect once. The fixed `search.list` request uses `part=snippet`,
`type=video`, `maxResults=10`, `order=date`, `regionCode=IN`, and a 30-day publication
window. Region IN concerns availability, not proof of filming in Karnataka. Search
is wording-based; YouTube's relevance can differ from feed literal matching. Google
quota applies; no fixed price/quota promise is made. Errors suggest checking API
enablement, key restrictions and quota. Configured does not mean live verified.

## Bounds and source provenance

- Fixed feed URLs: `https://www.thehindu.com/news/national/karnataka/feeder/default.rss`
  and `https://citizenmatters.in/feed/`. Both returned HTTP 200 XML during setup on
  16 October 2026. This is a point-in-time check, not an availability guarantee.
- Citizen Matters is multi-city reporting; query filtering is necessary.
- One request per click; 30-second timeout, 1 MB body cap, no redirects/retries.
  XML DTD/entities are disabled. Only publisher-host HTTPS article links are kept.
  The connector does not accept arbitrary network destinations.
- Up to 100 RSS/Atom entries examined, first 10 matching supported entries handled.
  Repeat runs preserve prior wording by source ID. These are current feed entries,
  not an archive search. Narrow the query if the first 10 matches are already retained.
- News: attributed headline, publication field, at most 1,000 excerpt characters,
  source link; HTML is converted to text and escaped when rendered. No full article
  fetching, paywall access, images or attachments. Publisher rights still apply.
- YouTube: title, channel, publication field and description excerpt with video
  link and stable video ID. No videos, transcripts, comments or thumbnails downloaded.
- Imports are owner-scoped pending inbox records. News has a distinct `News/RSS`
  channel. Deduplication does not update previously captured wording.
- Collector never registers cases, publishes reports, or contacts authors. These
  sources and X use local review/drafts and are excluded from automatic AI preparation
  and external-AI case briefs. Text remains untrusted source material.
- No claim of all social posts, Karnataka-wide coverage, inferred GPS, or verified
  complaint detection. Facebook and Instagram remain outside this connector.

## Validation

`PublicSourceTests` covers login, role and CSRF enforcement, explicit collection,
stale settings, encrypted/redacted keys, fixed API requests, limits, provider errors,
RSS/Atom parsing, Kannada, unsafe links, XML entities, malformed/oversized payloads,
owner isolation, duplicate preservation and review-to-local-draft/AI exclusions.
Provider fixtures do not establish live YouTube access; a locally entered valid key
and successful user request are required.

Validation on 16 October 2026: 450 .NET tests and 67 browser-script tests passed.
Live feed format checks found 60 The Hindu entries (41,211 bytes) and 10 Citizen
Matters entries (16,539 bytes), all with HTTPS article links. No live YouTube query
has been made and these feed checks did not create records in the user's workspace.

References:
- [YouTube search.list](https://developers.google.com/youtube/data/reference/rest/v3/search/list)
- [Google API key headers](https://docs.cloud.google.com/docs/authentication/api-keys-use)

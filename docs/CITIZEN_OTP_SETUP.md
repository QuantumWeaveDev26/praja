# Citizen mobile OTP setup plan

Status: not connected. The public /CitizenLogin page clearly directs current demo users to private tracking codes. No phone number, mock OTP, development bypass, or staff credential is accepted as mobile verification.

## Inputs required before implementation can be activated
- Select and provision an SMS verification provider for the organisation.
- Complete that provider's sender and message-template onboarding for the intended recipients and region. Confirm its current requirements directly with the provider.
- Supply server-side credentials through a secret store or local configuration UI, never through chat, source control or browser JavaScript.
- Confirm the approved message wording, expiration, sending budget and support/recovery process.
- Provide a designated test phone number and authorisation for test SMS delivery.

## Required authentication design
Use a separate Citizen authentication scheme and cookie, with no Officer role or access to /Work, /Automation, settings or staff APIs. Store a provider verification reference rather than a reusable plaintext OTP. Enforce send/resend limits by phone and IP, attempt limits, expiry and single-use verification. Use generic responses to avoid account enumeration. Log delivery outcome without phone numbers or codes. Apply a global spend cap and provider timeout/retry policy without sending duplicate SMS.

Bind new submissions to the verified citizen identity. OTP proves control of a phone, not ownership of every complaint mentioning that phone. Existing private-code cases need an explicit claim flow using a valid case code or staff-assisted verification; never auto-link them by matching unverified text. A citizen can access only their own linked complaints, evidence and decisions. Keep officer authentication entirely separate.

## Acceptance before enabling
Mocked provider tests: expired, incorrect, reused and rate-limited codes; callback tampering; provider failures; delivery retry deduplication; session fixation; logout; cross-citizen access and staff privilege denial. Then an authorised real SMS test and end-to-end submission/closure with separate citizen and staff sessions. No live success claim until these pass.

## Deployment addresses
The prototype uses one local server: /Submit for public intake, /CitizenLogin for citizen access, /Track for private case access, and /Account/Login for staff. Separate production hostnames can point to separate frontends backed by the same application services, but require actual domains, HTTPS, host restrictions, cookie scoping and deployment configuration. No domains have been provisioned by this work. Different URLs alone are not a security boundary; server-side authorisation is mandatory.

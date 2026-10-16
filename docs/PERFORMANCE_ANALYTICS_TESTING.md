# Department performance and recurring complaints

Open **Analytics** (`/Analytics`) in the officer workspace. Select the creation
period, district and service; sample cases are excluded unless explicitly included.
The existing workspace and role restrictions apply. No new external service,
database migration, citizen data publication or workflow mutation is involved.

## Department performance

Registered cases are grouped by their current department. The table shows total,
open, closed, awaiting assignment, overdue, awaiting citizen, recorded reopening,
average closure time and oldest open age. Drafts are excluded. Awaiting assignment
means Registered with no workflow. Overdue requires an open workflow case with a
deadline earlier than now and no earlier than case creation.

Closure time uses only Closed cases with a recorded closure between creation and
now. Missing/invalid timestamps are excluded and the usable sample count is shown.
Time spans original creation through latest closure, including transfers and repair
rounds. A recorded valid reopening counts the case once, not its number of rounds.
Transfers count under the current department, so this is not historical attribution
or an officer ranking. The selected period filters creation, not closure dates.

## Recurring patterns

At least two non-draft cases must share service, issue code, district and locality.
District/locality keys normalise Unicode, casing and whitespace. Missing locations,
missing issues and cases awaiting service review are excluded. Open the case list
to investigate each underlying case. After-closure counts each case created strictly
after another valid closed case in the same filtered group, once.

Groups are investigative hints, not confirmed duplicates or failed repairs. Different
spellings and records outside the selected period can hide recurrence. Linked inbox
duplicates are not additional registered cases. No automatic merge, escalation or
reopening occurs. Existing CSV exports case rows, not these summary tables.

## Automated validation

Run `./test.ps1`. PerformanceAnalyticsTests covers arithmetic, due/reopening
boundaries, invalid closure times, filtered samples and creation periods, recurrence
normalisation, after-closure ordering, foreign workflow isolation, relational data
loading and authenticated HTML output with owner and service filters. Existing
workflow and access-control tests run alongside these tests. All fixtures use
isolated databases; no demonstration or live cases/accounts are created.

On 16 October 2026, the full suite passed 411 .NET tests and 67 browser-script
tests. The officer/reviewer/citizen workflow test was then extended and passed
with analytics assertions at awaiting-citizen and final citizen-approved closure,
including the existing citizen dispute and second-repair path.

Real data quality and historical routing completeness still need evaluation.

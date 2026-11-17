# ADR 0002: One Cosmos container with a `type` discriminator

Status: Accepted

## Context
A case has related items that are read together and written together: the case, its documents, the final answers, the agents' proposals, the audit entries and the markers of processed idempotency keys. Some writes must succeed or fail as one unit, for example "store the analyst's answer, mark the proposal Accepted and append the audit entry".

## Decision
Store everything in one container `cases` with partition key `/caseId`. Each item carries `type` (`case`, `document`, `answer`, `proposal`, `audit`, `message`). Ids are deterministic (`{caseId}:answer:{questionId}`, `{caseId}:proposal:{questionId}`, `{caseId}:audit:{seq}`). Every write uses one `TransactionalBatch` on the case partition that also replaces the `case` item with `IfMatchEtag`. The case `_etag` is therefore the version of the whole case: any write (a proposal, an answer, a take over) changes it. The InMemory store follows the same contract with one lock.

## Consequences
- Transactional batches only work inside one partition of one container, so this layout is what makes atomic writes possible.
- Because every write bumps the case `_etag`, a writer that read an older version always gets a 409. This is what makes "take over" and "analyst edits while the agent is working" safe without extra locks.
- Reading a case is one single-partition query; lists across cases are cross-partition queries, acceptable at this volume.
- The audit trail has no update or delete method in the store interfaces, and no such route in the API; tests check both.
- A transactional batch holds at most 100 operations; each write here touches only a few items, so a questionnaire with a few hundred questions still fits.

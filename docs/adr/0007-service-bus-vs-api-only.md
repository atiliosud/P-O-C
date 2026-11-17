# ADR 0007: Service Bus and the API, not the API only

Status: Accepted (the PoC uses an in-memory bus with the same guarantees)

## Context
The case application and the AI backend talk in two ways. Some calls need an answer now: read a case, write a proposal, record an analyst decision. Other work is long or can wait: answering a whole questionnaire takes minutes, and an analyst decision has to trigger a re-check of the dependent questions. If everything goes through synchronous API calls, the case application waits on the models, a slow or failed model call blocks the analyst, and each side has to know whether the other is up.

## Decision
- Synchronous calls go through the Case API: reads, proposal writes and analyst decisions, each with `If-Match` and, for agent writes, an `Idempotency-Key`.
- Long work and analyst decisions go through events: `case.ready` starts a run, `answer.confirmed`, `case.takeover` and `case.released` tell the AI backend what changed. In production this is Azure Service Bus with sessions keyed by `caseId` and a dead-letter queue (see [ADR 0004](0004-events-between-case-app-and-ai-backend.md)).
- Events carry ids, not data. The handler re-reads the case through the API, so the API stays the only path to the case.

## Alternative considered
Start with the API only: the case application calls a webhook on the AI backend when a case is ready, and polls a status endpoint (or the backend calls back) when the run finishes. This is simpler to run at first: no queue, fewer moving parts. It was not chosen because retries, ordering per case and "the other side is down" would then live in custom code on both sides. It is a reasonable first step if Service Bus is not available yet; the handlers stay the same because they already re-read the case and are idempotent.

## Consequences
- The analyst never waits on a model. If the AI backend is down, events queue and cases show "AI pending".
- There is a queue to operate, with dead-letter alerts.
- Moving between the webhook option and Service Bus changes the transport, not the handlers.

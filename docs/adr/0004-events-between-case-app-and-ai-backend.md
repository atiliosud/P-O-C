# ADR 0004: Events between the case application and the AI backend

Status: Accepted (for the PoC; production uses Service Bus)

## Context
The two sides must be able to fail independently. When a case is marked ready, the AI backend must hear about it, and when an analyst decides, the backend must re-check the dependent questions. If the AI backend is down, analysts keep working as they do today. If the case application is down, the backend must not lose its proposals. Direct calls in both directions would couple the two sides.

## Decision
Both directions use events on an `IEventBus`: `case.ready`, `case.takeover`, `case.released`, `answer.confirmed`, `proposal.rejected`. The PoC implements it in memory with the guarantees the design needs from Service Bus:
- every event has a `MessageId` (consumption is idempotent) and a per-case sequence number (an out-of-order event is held until the gap fills; after a timeout the gap is skipped and counted);
- handlers are retried, then the message is dead-lettered; the reviewer app shows the dead-letter count;
- events are triggers, not data: the handler re-reads the case from the Case API and decides from the current state, and every event first re-checks the dependents of every confirmed parent, so a repeated event changes nothing and a skipped `answer.confirmed` is caught up by the next event of the case.

Writes back to the case application are idempotent too: the backend sends an `Idempotency-Key` (`{messageId}:{questionId}`) and the API ignores a repeat. When the Case API is unreachable, the proposals wait in an outbox in the backend and are sent later with the same keys.

## Consequences
- Each side can be down without blocking the other; the cost is some delay and a queue to operate.
- The in-memory bus cannot cross processes, so the PoC runs the AI backend inside the Case API process. It still talks to the case application only through HTTP and the bus.
- In production: Azure Service Bus with sessions keyed by `caseId` (ordering per case) and a dead-letter queue with an alert; Durable Functions for the orchestration state.

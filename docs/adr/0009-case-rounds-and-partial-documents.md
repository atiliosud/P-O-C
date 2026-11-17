# ADR 0009: Case rounds and partial documents

Status: Designed, not built yet

## Context
Real cases rarely arrive complete. The first round has some documents, the AI backend proposes what it can and flags what is missing, the client is asked for more, and new documents arrive days later. Re-running the whole questionnaire on every new document wastes model calls, and it can replace proposals an analyst has already looked at.

## Decision
The case state holds three things:
- **Versioned documents.** A new upload is a new version; nothing is overwritten. Each proposal cites the document version it used.
- **Facts with provenance.** A fact (for example "account country: Switzerland") keeps the document version, page and quote it came from, and which questions used it.
- **Open requests to the client.** Each request names the missing document or fact and the questions waiting on it. It is closed when a document that satisfies it arrives.

When a new document arrives, the backend works out which questions it can affect (the questions waiting on an open request it satisfies, plus questions whose cited document it replaces) and re-runs only those. Questions with a final answer are not touched; if new evidence contradicts a final answer, the analyst is told, and the value does not change.

Each round is one run of the orchestrator, triggered by an event (`document.added`). In production the orchestration waits between rounds with Durable Functions `WaitForExternalEvent`.

## Consequences
- Model calls scale with what changed, not with the size of the questionnaire.
- The audit can show which round and which document version produced each proposal.
- More state to keep consistent: document versions, facts and requests have to be written in the same atomic batch as the proposals they affect.
- Not in the PoC: a submitter can add documents to an open case, but there are no document versions, facts or open requests, and a run covers all open questions.

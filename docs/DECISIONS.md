# Decisions

Short architecture decision records are in [docs/adr](adr). This file lists the key decisions at a glance and the assumptions made while building this version.

## Key decisions

| Decision | Choice | Why | ADR |
|---|---|---|---|
| Orchestration | Fixed workflow in code; models only answer | Auditable; the code decides the question order | [0001](adr/0001-workflow-not-orchestrator.md) |
| Storage layout | One container `cases`, `type` discriminator, partition `/caseId` | Atomic batch per case; every write changes the case version | [0002](adr/0002-single-container-with-type.md) |
| Confidence | Token probability (yes/no) or verifier score (free text) | Checkable against analyst decisions | [0003](adr/0003-logprob-confidence.md) |
| Integration | Events with message id, per-case sequence, retries and dead letters | Each side can be down without blocking the other | [0004](adr/0004-events-between-case-app-and-ai-backend.md) |
| Assistant access | Read-only MCP tools | Assistants look up, the API decides | [0005](adr/0005-read-only-mcp.md) |
| Final values | Proposals are separate from final answers | An agent can never overwrite a person | [0006](adr/0006-proposals-separate-from-final-answers.md) |
| Transport | API for synchronous calls, events for long work and decisions | The analyst never waits on a model | [0007](adr/0007-service-bus-vs-api-only.md) |
| Question dependencies | Versioned data, enforced server-side, never decided by the model | One source of truth for applicability | [0008](adr/0008-question-dependencies-as-versioned-data.md) |
| Rounds and partial documents | Versioned documents, facts with provenance, open requests (not built) | Re-run only the affected questions | [0009](adr/0009-case-rounds-and-partial-documents.md) |

## Assumptions

Calls made for this version, with the cost if wrong where it matters.

1. The compliance questionnaire replaces the domain of an earlier version of this PoC; it does not live next to it.
2. One process hosts the Case API and, in the same process, the AI backend (bus consumer, orchestrator, agents, outbox), in both storage modes. The in-memory bus cannot cross processes. The backend lives in its own library (`CaseAgents.Agents`) and reaches the case application only through HTTP and the event bus, so the boundary is real. In production it is a separate app (Durable Functions plus Service Bus sessions). The change-feed Worker of the earlier version is gone.
3. The AI backend calls the Case API over HTTP, even though it is in the same process, with a `Case.Agent` token scoped to one case (`case_id` claim). In production the orchestrator or APIM would do the scoping.
4. The AI backend runs only with `Auth:Mode=Dev`, because it needs to mint its own agent tokens. With Entra auth the API logs that the AI backend is disabled and does not start it; in production it is a separate app with its own managed identity. With the in-process backend off and no other consumer, events stay queued for a consumer, as they would on a real queue, so Ready cases keep showing "AI pending": the banner means "the AI backend has not processed this case yet".
5. Proposals never overwrite finals: a proposal write for a question that has a final answer, or that an analyst already accepted or rejected, gets a 409. Agents only propose on untouched questions.
6. Every write to a case changes the case ETag (the case item is replaced in the same batch), so one rule covers every race: write with the version you read, on 409 re-read and re-evaluate, never force.
7. Take over sets `aiLocked` and bumps the version in one batch; release clears the lock and emits an event so the backend continues. Analysts can still decide while a case is taken over.
8. Events are triggers. Handlers re-read the case and decide from its current state, and every handled event first re-checks the dependents of every confirmed parent. So a duplicate is ignored, and a late or skipped `answer.confirmed` is caught up by the next event of that case (a late event below the expected sequence is dropped and counted as a duplicate). When a sequence gap is not filled within the timeout, it is skipped and counted instead of blocking the case. If a run gets "duplicate" back for an idempotency key it already used, it stops instead of writing the same question again; the next event continues.
9. Idempotency keys are `{messageId}:{questionId}` (`:withdraw:` or `:confirm:` for dependent re-checks). The API stores a marker item in the same batch as the write, so a repeated key writes nothing.
10. A `NeedsAnalyst` proposal never has a value. It keeps the quote only when the quote was found literally on the cited page. A `Proposed` write needs a confidence band and a confidence at or above `proposalThreshold`, and a withdrawal needs an existing proposal (the API answers 422 and 409 otherwise).
11. The quote check is done in code on whitespace-normalised, case-sensitive text. The audit stores the document, page, offset and a SHA-256 hash of the normalised quote, plus the quote itself for display.
12. The Fake answerer is a keyword device: it picks the first line that contains one of the question's `evidenceKeywords`. That is why it proposes Yes for Q4 from the line "The account held with Example Bank (Zurich) was closed on 14 March 2024.", a plausible error that makes the case for analyst review. It is a test double, not a model.
13. The seeded story is anchored to a dev clock: `POST /dev/clock` (Development only) makes the app read 17 Nov 2026 and keep ticking. The seed creates cases at 10:30 UTC and plays the analyst decision at 10:42 UTC, so the audit shows "17 Nov 10:42". The live-demo case is left undecided; decisions you make on it show the current dev-clock time.
14. "Case app down" is simulated as a 503 to `Case.Agent` calls only. The reviewer app keeps working. The AI backend puts what it could not send in an in-memory outbox and flushes it later. If a replay throws (for example a model timeout), the entry goes back in the outbox and is retried up to 5 times, then dropped with an error log. The outbox is lost on a restart; production would use durable state.
15. "AI backend down" pauses the bus consumer and the outbox flusher: events and queued writes wait, and cases show the "AI pending" banner.
16. Shadow proposals (`AiBackend:ShadowMode=true`) are written for evaluation only. They are hidden from analysts in every view, but returned to the Eval.
17. Agents may read their own case through the API; they get 404 for any other case, and a token without a case scope reads nothing.
18. A caller who may not read a case gets 404, not 403, so its existence is not leaked.
19. The public Cosmos emulator key is allowed in exactly one test file, with a comment, because Microsoft publishes it and it is not a secret.
20. Dev scripts build once and start apps with `dotnet run --no-build --no-launch-profile` to avoid build races. The host port of the emulator can be changed with `COSMOS_EMULATOR_PORT`.
21. `Auth:Mode=Dev` is set by the dev scripts, the tests and `launchSettings.json`, not by `appsettings.Development.json`, so an app deployed with `ASPNETCORE_ENVIRONMENT=Development` does not silently enable dev tokens.
22. The Cosmos integration tests are skipped unless `CASEAGENTS_COSMOS_TESTS=1`.
23. The seed waits up to 90 s for the AI backend, because in Cosmos mode the emulator can be slow to answer.
24. Correcting a final answer is allowed and audited with the previous final value as `before`. A dependent's final answer that stops applying after its parent is corrected is kept but set aside: it does not count as answered and is shown as "Set aside" (ADR 0006). Production might add a rule that the analyst confirms or clears set-aside values before the case is closed.

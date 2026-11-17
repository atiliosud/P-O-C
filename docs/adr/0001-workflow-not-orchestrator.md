# ADR 0001: A fixed workflow, not an agent orchestrator

Status: Accepted

## Context
Answering a compliance questionnaire has known steps: read the documents, pick the next open question, answer it with a quote, verify the quote, propose or flag. The only non-deterministic parts are the answer and the verdict of the second model. An agent framework that lets a model pick tools and next steps would add cost, latency and variability to a process that does not need it, and would make it harder to explain to a regulator why a value was proposed.

## Decision
The order is plain code. `CaseOrchestrator` asks `QuestionRules.NextToPropose` for the next question (questionnaire order, respecting `dependsOn`), calls the Answerer for that one question, checks the quote in code (`EvidenceLocator`), asks the Verifier whether the quote supports the answer, and lets `ProposalPolicy.Decide` produce a `Proposed` or `NeedsAnalyst` result. The models answer questions; they never decide what happens next. Extractor, Answerer, Verifier and Gap finder are logical roles that run as one service.

## Consequences
- Every proposal is reproducible from the stored evidence, confidence and thresholds; the rules are unit tested.
- Behaviour is auditable and cheap: one answer call and one verifier call per question.
- Adding a step (a second answerer, a re-ranker) means editing code, not prompting an agent.
- Free-form multi-step reasoning is out of scope. If a future case needs it, it should be an explicit step behind the same workflow, justified by the evaluation results.

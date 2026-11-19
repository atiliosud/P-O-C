# ADR 0006: Proposals are separate from final answers

Status: Accepted

## Context
The regulator needs to see, for each value, who set it (a person or an agent) and on what basis. The case application is the system of record and a person confirms every final value. If an agent could write the same field an analyst writes, a late or repeated agent write could overwrite a human decision, and the audit trail could not tell the two apart.

## Decision
Two kinds of items, two owners:
- `answer` (final value): written only by people, through `POST /api/cases/{id}/answers/{questionId}` with the `Case.Review` role. It stores the value, who set it, when and why.
- `proposal`: written only by the AI backend, through `PUT /api/cases/{id}/proposals/{questionId}` with a `Case.Agent` token scoped to one case. One per question, replaced on change. It stores value, quote and where it was found (document, page, offset, hash), confidence and band, agent, prompt version, model version, verifier result and status (`Proposed`, `NeedsAnalyst`, `Withdrawn`, `Accepted`, `Rejected`).

Rules enforced in the API and tested:
- A proposal write for a question that already has a final answer is refused (409 `final_answer_exists`), and one for a question an analyst already accepted or rejected is refused (`touched`). Agents only propose on untouched questions.
- A `NeedsAnalyst` proposal has no value. The edit form for it starts empty.
- When a parent question is confirmed with a value that does not match a dependent's `dependsOn`, the dependent proposal becomes `Withdrawn` with a reason; nothing is deleted. When it matches, the dependent becomes a normal proposal that still needs a person.
- Every write replaces the case item, so every write changes the version (ETag), and an agent write based on an older version fails with 409.
- "Take over" sets `aiLocked` and changes the version in the same batch: new agent writes are refused, and a write already in flight fails the version check.
- Every action appends an audit entry in the same atomic write. The audit view shows, for each question, the proposal, its basis, the final value and who set it.
- An analyst can correct a final answer later. The `answer_confirmed` entry records the value it replaced as `before`: the previous final value on a correction, the proposed value on the first decision. The proposal snapshot is kept on both.
- A final answer whose question stops applying (Q4.1 after Q4 is corrected from Yes to No) is set aside, not deleted: only people remove people's values. It no longer counts as answered, the reviewer app marks it "Set aside: no longer applies", and it applies again if the parent changes back.

## Consequences
- An agent can never change a final value, by construction and by test.
- The same screen shows both the agent's proposal and the person's decision, and the audit view can reconstruct the story (proposed Yes, set to No by an analyst, dependents withdrawn).
- Cost: two sets of items to keep consistent, and the analyst decision has to be sent back to the AI backend as an event so that dependent questions are re-checked.

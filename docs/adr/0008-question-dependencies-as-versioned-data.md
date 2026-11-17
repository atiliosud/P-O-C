# ADR 0008: Question dependencies are versioned data

Status: Accepted (version pinning is designed, not built)

## Context
Many questions apply only when an earlier answer has a certain value (Q4.1 to Q4.3 apply only if Q4 = Yes). These rules decide which questions a person must answer, so the regulator has to be able to see which rules applied to a case. If the rules live in prompts, the model can apply them differently from run to run. If they are copied into the case application and the AI backend separately, the two copies drift.

## Decision
- The dependencies are defined once, as data, next to the questions: `dependsOn` in `config/questionnaire.json` (`{ "questionId": "Q4", "value": "Yes" }`).
- The case application enforces them on the server. In the PoC the Case API refuses a final answer to a question that does not apply (409 `not_applicable`). In a Dataverse case application this would be a server-side plug-in reading the same rules, not a form script.
- The orchestrator reads the same file and the same version, through `QuestionRules`, to decide which question to propose next and which proposals to withdraw.
- The model never decides whether a question applies. It answers one question at a time; the code picks the question.
- Each case is pinned to the questionnaire version it started with. A new version applies to new cases; open cases keep theirs unless someone migrates them on purpose.

## Consequences
- One source of truth for applicability, readable by people and testable in code (`QuestionRulesTests`).
- The audit can name the questionnaire version that applied to a case.
- Changing a rule is a data change with a version, reviewed like code.
- Not built in the PoC: the questionnaire has a `promptVersion` but no version of its own, and cases are not pinned yet. The API and the orchestrator load the same file at start-up.
- This is the open question for the real project: where the rules live in the case application and who owns their changes.

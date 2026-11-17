# ADR 0003: Confidence from token probabilities and a verifier, not from the model's own score

Status: Accepted

## Context
The analyst needs a confidence badge, and the code needs a threshold below which a proposal carries no value. One option is to ask the model to write "confidence: 0.93". That number is generated text: it is not calibrated, tends to be high regardless of correctness, and cannot be checked against anything.

## Decision
- yes_no questions: the Answerer's confidence is `p = exp(logprob)` of the first token that reads "Yes" or "No" (`logprobs: true`, `temperature: 0`, strict JSON schema). No such token means no confidence, which makes the proposal `NeedsAnalyst`.
- free_text questions: there is no single answer token, so the confidence is the verifier's score, the probability that the quote supports the answer.
- Bands come from the questionnaire config: High at or above `highConfidence` (0.85), Medium at or above `proposalThreshold` (0.60), otherwise Low. Low means `NeedsAnalyst` and no value.
- The Fake models return fixed values (Q4 = 0.93) so the demo and the tests are deterministic.

## Consequences
- The probability comes from the model's own output distribution and can be checked against analyst decisions: the Eval console reports precision per question type.
- Thresholds live in `config/questionnaire.json` and change without code changes.
- Requires a deployment that returns logprobs; the startup check fails with a clear message otherwise.
- A high token probability is not the same as a correct answer. In the demo scenario both models are confident and wrong, which is why a person confirms every final value and why the Eval reports the "accepted unchanged" rate as a risk signal.

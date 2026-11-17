# Adapting the PoC to a real case study

The workflow is generic. When the real questionnaire arrives, most changes are in `config/questionnaire.json`. Paths are relative to the repository root.

## What changes

### 1. The questionnaire
File: `config/questionnaire.json` (loaded by `QuestionnaireLoader`, which rejects an invalid file at startup with a clear message; override the location with `Questionnaire:Path`).
- `questions[]`: `id` (unique), `text`, `type` (`yes_no` or `free_text`), `required`, optional `dependsOn: { questionId, value }`, and `evidenceKeywords`. The order in the file is the order the AI backend proposes in.
- `dependsOn` must point to an EARLIER `yes_no` question, and `value` must be `Yes` or `No`. A dependent question applies only when its parent's FINAL answer equals `value`; until the parent is confirmed its proposal is marked "depends on Q4, not confirmed yet".
- `requiredDocuments[]`: `kind` and `name`. A missing kind is reported as a gap. The `kind` of each attached document must match.
- Gaps (`GapFinder`): a missing required document, and a required, applicable question without a final answer whose proposal is `NeedsAnalyst` ("no evidence found in the documents"). A question with no proposal yet is not a gap, so a case still waiting for the AI backend does not list every question.
- `evidenceKeywords`: words that select the passages sent to the Answerer (the per-case index returns up to three pages that contain any of them). With the Fake answerer they also pick the quote, so choose words that appear in the text of your documents.
- `proposalThreshold` (default 0.60): below it a proposal becomes `NeedsAnalyst` with no value. `highConfidence` (default 0.85): at or above it the badge is High. Start high; tune with the Eval console on analyst decisions.
- `promptVersion` (seed: `v12`): recorded on every proposal and shown in the audit view. Change it whenever the Answerer prompt or schema changes, so the audit shows which version produced a value.

### 2. Documents
Documents are text per page, as already extracted (`{ documentId, name, kind, pages: [{ n, text }] }`). In production Document Intelligence produces this; here the demo documents are in `config/scenario-documents.json`, which the tests and the seed also read. To use real files, add them through `POST /api/cases/{id}/documents` (the seed script shows the shape).

### 3. The prompt
`src/CaseAgents.Agents/Models/AnswererPrompt.cs` builds the system and user messages. Keep three things when editing: one question per call, document text inside the `<untrusted_document>` block, and the fixed JSON schema in `AnswerSchema.cs` (value, quote, documentId, page). Output that does not match the schema is rejected and becomes `NeedsAnalyst`.

### 4. Switch to Azure OpenAI
Set `Models:Mode=AzureOpenAI`, `AzureOpenAI:Endpoint`, `AzureOpenAI:Deployment` (must return logprobs), optionally `AzureOpenAI:VerifierDeployment` for a second, different deployment, and `AzureOpenAI:ApiVersion`. Authentication is `DefaultAzureCredential`: no keys. The app checks both deployments at startup and fails with a clear message if one is missing or does not return logprobs. Use `AiBackend:ShadowMode=true` first, so proposals are recorded but analysts do not see them, then compare with the Eval.

### 5. Switch to Cosmos DB
Set `Storage:Mode=Cosmos` and either `Storage:Cosmos:AccountEndpoint` (Azure, `DefaultAzureCredential`) or `Storage:Cosmos:ConnectionString` with `Storage:Cosmos:EmulatorMode=true` (local emulator, `docker compose up -d`, or `scripts/dev.ps1 -Cosmos`). `EnsureCreated=true` creates the database and container.

### 6. Roles
- `AppRoles` in `src/CaseAgents.Core/Security/CaseAccess.cs` (`Case.Submit`, `Case.Review`, `Case.Agent`) and `AuthPolicies` in `src/CaseAgents.Infra/Auth/AuthSetup.cs`.
- Entra app registration: define the same app roles and assign them to users, groups and the AI backend's managed identity.
- APIM: update the `required-claims` list for the `roles` claim in `infra/apim/policy.xml`.
- Ownership rule (`CaseAccess.CanRead`): adjust if submitters should see more or fewer cases.

### 7. Seed and demo data
- `scripts/seed-cases.json`: the historical cases (document edits and the analyst decisions to play). Replace with representative cases; keep a mix of accepted and corrected proposals so the Eval has both.
- `config/scenario-documents.json`: the scenario documents shared by the tests, the seed and the demo. If you change a quote, update `tests/CaseAgents.Tests/TestSupport/Scenario.cs` and `docs/DEMO.md` too.

## What stays the same

| Area | Where | Why it does not change |
|---|---|---|
| Workflow (next question, answer, verify, propose) | `src/CaseAgents.Agents/Orchestration` | Driven by the questionnaire and its dependencies |
| Business rules (applicability, proposal policy, gaps, audit view) | `src/CaseAgents.Core/Rules` | Pure functions over the questionnaire |
| Storage | `src/CaseAgents.Infra/Storage/**` | One container with a `type`; items keep the same shape |
| Event bus | `src/CaseAgents.Infra/Messaging` | Message ids, per-case sequence, retries, dead letters |
| Auth plumbing | `src/CaseAgents.Infra/Auth/AuthSetup.cs` | Roles are data; Entra and Dev modes stay |
| MCP server | `src/CaseAgents.Mcp/**` | Read-only tools over the same case view |
| Eval console | `src/CaseAgents.Eval/**` | Compares proposals with final values for any questionnaire |
| Infrastructure | `infra/**` | Cosmos, identities and APIM are independent of the questions |

## Checklist
1. Edit `config/questionnaire.json` and `config/scenario-documents.json` together, then update `scripts/seed-cases.json`. Some tests depend on the shipped scenario (`tests/CaseAgents.Tests/TestSupport/Scenario.cs`); run `dotnet test`.
2. Run `./scripts/dev.ps1 -Seed` (or `./scripts/dev.sh --seed`) and look at the cases in the reviewer app.
3. Switch to Azure OpenAI, run with `AiBackend:ShadowMode=true` on real cases, collect analyst decisions, run the Eval, then set the thresholds.

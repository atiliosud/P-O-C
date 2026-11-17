# Azure infrastructure (reference only)

This folder describes how the PoC would run in Azure. It is **not deployed** by this PoC; it only has to compile.

## Resources (`main.bicep`)
- Cosmos DB account (serverless, local auth disabled), database `caseagents`, containers `cases` (`/caseId`) and `leases` (`/id`; not used by the current code, which has no change-feed worker).
- Two user-assigned managed identities (least privilege, both scoped to the `caseagents` database, not the whole account):
  - `<baseName>-id-app` (Case API and, in this PoC, the AI backend that runs in the same process): Cosmos DB Built-in Data Contributor, plus "Cognitive Services OpenAI User" on an existing Azure OpenAI account (only when `openAiAccountName` is set).
  - `<baseName>-id-mcp` (MCP server): Cosmos DB Built-in Data Reader only.
- API Management (tier from `apimSku`, capacity 1) with two backends (`case-api`, `mcp-server`), one API with wildcard operations, named values and the policy in `apim/policy.xml`.

## Policy (`apim/policy.xml`)
Validates the Entra ID token (`validate-azure-ad-token`, role `Case.Submit`, `Case.Review` or `Case.Agent`), rate-limits per caller (`rate-limit-by-key` on the `oid` claim), and routes `/cases*`, `/questionnaire`, `/eval*` and `/ops*` to the Case API (rewritten to `/api/...`) and `/mcp*` to the MCP server. The API has wildcard GET, POST and PUT operations (the AI backend writes proposals with PUT).

Named values created by the template and used by the policy: `tenant-id`, `api-audience`, `client-app-id`.

`rate-limit-by-key` is not supported in the Consumption tier ([policy reference](https://learn.microsoft.com/azure/api-management/rate-limit-by-key-policy)), so `apimSku` allows only `Developer` (default, cheapest, no SLA), `BasicV2` or `StandardV2`.

## Parameters
Required: `caseApiUrl`, `mcpServerUrl`, `apiAudience`, `clientAppId`, `publisherEmail`.
Optional: `baseName`, `location`, `tenantId`, `publisherName`, `openAiAccountName`, `apimSku`.

## Outputs
`cosmosEndpoint`, `apimGatewayUrl`, and for each identity (`appIdentity*`, `mcpIdentity*`) the `ClientId`, `PrincipalId` and `ResourceId`. Set the client id as `AZURE_CLIENT_ID` on the matching app so `DefaultAzureCredential` picks the right identity.

## The AI backend identity

In the PoC the AI backend runs inside the Case API process and uses the `id-app` identity. In production it is a separate app (orchestrator plus agents) with its own managed identity that has only two things: the "Cognitive Services OpenAI User" role and the `Case.Agent` app role, so it reaches case data only through APIM and the Case API. It has no Cosmos role of its own for case data. The Bicep here does not create that third identity yet.

## Known limitations
- `Case.Agent` tokens in the PoC carry a `case_id` claim that limits them to one case. Entra app roles cannot express that, so in production the orchestrator or APIM has to enforce the case scope (for example an APIM policy that compares the path with a claim issued per run). This folder does not implement it.
- The backends (`caseApiUrl`, `mcpServerUrl`) are public URLs, so a caller with a valid token can bypass APIM and skip its rate limit and client-app allow-list. Roles and case ownership are still enforced by the backends. To close this, restrict backend ingress to APIM (the `ApiManagement` service tag or private endpoints), or validate the `azp`/`appid` claim in the backends.
- The web app calls `/api/*`, while APIM exposes `/cases`, `/questionnaire`, `/eval` and `/ops` and has no CORS policy. The SPA is therefore not wired to APIM in this PoC; locally it uses Dev login.

## Validate
```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
```

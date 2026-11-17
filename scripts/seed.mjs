// Loads the demo cases through the Case API and plays the analysts' decisions. Node 20+, built-in fetch only.
// Usage: node scripts/seed.mjs [--api http://localhost:5080]
//
// Needs the API in Development with Auth:Mode=Dev (dev tokens and the dev clock) and the AI backend enabled.
import { readFile } from 'node:fs/promises';

const argIndex = process.argv.indexOf('--api');
const api = (argIndex > 0 ? process.argv[argIndex + 1] : 'http://localhost:5080').replace(/\/$/, '');
const seedFile = new URL('./seed-cases.json', import.meta.url);
const documentsFile = new URL('../config/scenario-documents.json', import.meta.url);

const WAIT_SECONDS = 90;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

class ApiError extends Error {
  constructor(method, path, status, text) {
    super(`${method} ${path} -> HTTP ${status}: ${text}`);
    this.status = status;
    this.text = text;
  }
  get code() {
    try {
      return JSON.parse(this.text).code;
    } catch {
      return undefined;
    }
  }
}

async function call(method, path, token, body, headers = {}) {
  let res;
  try {
    res = await fetch(`${api}${path}`, {
      method,
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...headers,
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (err) {
    throw new Error(`Cannot reach ${api} (${method} ${path}): ${err.message}. Is the API running?`);
  }
  const text = await res.text();
  if (!res.ok) throw new ApiError(method, path, res.status, text);
  return text ? JSON.parse(text) : null;
}

async function tokenFor(userId, role) {
  const result = await call('POST', '/dev/token', null, { userId, roles: [role] });
  return result.accessToken;
}

const setClock = (at) => call('POST', '/dev/clock', null, { at });

// ---- case helpers -----------------------------------------------------------------------------------------------

const clone = (value) => JSON.parse(JSON.stringify(value));

async function createReadyCase(token, title, documents) {
  let item = await call('POST', '/api/cases', token, { title });
  const caseId = item.caseId;
  for (const doc of documents) {
    item = await call('POST', `/api/cases/${caseId}/documents`, token, doc, { 'If-Match': item._etag });
  }
  await call('POST', `/api/cases/${caseId}/ready`, token, undefined, { 'If-Match': item._etag });
  return caseId;
}

const getView = (caseId, token) => call('GET', `/api/cases/${caseId}`, token);
const stateOf = (view, questionId) => view.questions.find((q) => q.question.id === questionId);

// True when no applicable question without a final answer is still waiting for a proposal.
function proposalsDone(view) {
  if (view.aiPending) return false;
  return view.questions.every(
    (q) => q.applicability === 'NotApplicable' || q.answer || q.proposal,
  );
}

async function waitFor(caseId, token, what, predicate) {
  const deadline = Date.now() + WAIT_SECONDS * 1000;
  for (;;) {
    const view = await getView(caseId, token);
    if (predicate(view)) return view;
    if (Date.now() > deadline) {
      throw new Error(`Timed out after ${WAIT_SECONDS} s waiting for ${what} (case ${caseId}). Is the AI backend running?`);
    }
    await sleep(500);
  }
}

// An analyst decision. The AI backend may write at the same time and change the case version: re-read and retry.
async function decide(caseId, token, questionId, action, value, reason) {
  for (let attempt = 0; attempt < 8; attempt++) {
    const view = await getView(caseId, token);
    const state = stateOf(view, questionId);
    if (!state) throw new Error(`Question ${questionId} does not exist (case ${caseId}).`);
    if (action === 'accept' && state.proposal?.status !== 'Proposed') {
      throw new Error(
        `Cannot accept ${questionId} on case ${caseId}: its proposal is ${state.proposal?.status ?? 'missing'}, not Proposed.`,
      );
    }
    try {
      await call('POST', `/api/cases/${caseId}/answers/${encodeURIComponent(questionId)}`, token, { action, value, reason }, {
        'If-Match': view.case._etag,
      });
      return;
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === 'version_changed') {
        await sleep(300);
        continue;
      }
      throw err;
    }
  }
  throw new Error(`Gave up deciding ${questionId} on case ${caseId}: the case kept changing.`);
}

// After a parent is confirmed, the AI backend re-checks its dependents. Wait until that has happened.
function dependentsSettled(parentId) {
  return (view) => {
    const parent = stateOf(view, parentId);
    if (!parent?.answer) return false;
    const dependents = view.questions.filter((q) => q.question.dependsOn?.questionId === parentId);
    if (parent.answer.value !== 'Yes') {
      return dependents.every((q) => q.answer || q.proposal?.status === 'Withdrawn' || !q.proposal);
    }
    return dependents.every((q) => q.answer || !q.proposal || !q.proposal.dependsOnUnconfirmed);
  };
}

// ---- main -------------------------------------------------------------------------------------------------------

async function main() {
  const seed = JSON.parse(await readFile(seedFile, 'utf8'));
  const docs = JSON.parse(await readFile(documentsFile, 'utf8'));
  const { submitter, analyst, secondAnalyst } = seed.users;

  const submitterToken = await tokenFor(submitter, 'Case.Submit');
  const analystToken = await tokenFor(analyst, 'Case.Review');
  const secondToken = await tokenFor(secondAnalyst, 'Case.Review');

  await setClock(seed.clock.create);

  const cases = [];
  const add = async (title, documents, extra = {}) => {
    const caseId = await createReadyCase(submitterToken, title, documents);
    cases.push({ title, caseId, ...extra });
    console.log(`  created: ${title}`);
  };

  console.log('Creating cases');
  await add('Accounts outside the EU (live demo)', clone(docs.scenario), { role: 'live' });
  await add('Accounts outside the EU (completed example)', clone(docs.scenario), { role: 'completed' });
  // No client declaration here: only the payslip's hidden text could "answer" Q3 and Q6, so both stay Needs analyst.
  const injectionDocuments = [...clone(docs.scenario).filter((d) => d.documentId !== 'client-declaration'), ...clone(docs.injection)];
  await add('Hidden instruction in a payslip', injectionDocuments, { role: 'injection' });
  for (const h of seed.historical) {
    const documents = clone(docs.scenario);
    for (const o of h.pageOverrides ?? []) {
      const doc = documents.find((d) => d.documentId === o.documentId);
      const page = doc?.pages.find((p) => p.n === o.page);
      if (!page) throw new Error(`seed-cases.json: no page ${o.page} in document '${o.documentId}'.`);
      page.text = o.text;
    }
    if (h.addSourceOfFunds) documents.push(clone(seed.sourceOfFundsDocument));
    await add(h.title, documents, { role: 'historical', decisions: h.decisions });
  }

  console.log('Waiting for the AI backend to propose answers');
  for (const c of cases) {
    await waitFor(c.caseId, analystToken, 'proposals', proposalsDone);
  }

  await setClock(seed.clock.decide);

  console.log('Analyst decisions');
  const completed = cases.find((c) => c.role === 'completed');
  const d = seed.scenarioDecision;
  await decide(completed.caseId, analystToken, d.questionId, 'edit', d.value, d.reason);
  await waitFor(completed.caseId, analystToken, 'Q4.1 to Q4.3 to be withdrawn', dependentsSettled(d.questionId));
  console.log(`  ${analyst}: ${d.questionId} -> ${d.value} on "${completed.title}"`);

  for (const c of cases.filter((x) => x.role === 'historical')) {
    for (const step of c.decisions) {
      await decide(c.caseId, secondToken, step.questionId, step.action, step.value, step.reason);
      // A parent decision makes the backend re-check its dependents; wait so the next accept sees the settled state.
      await waitFor(c.caseId, secondToken, `dependents of ${step.questionId}`, (view) => {
        const hasDependents = view.questions.some((q) => q.question.dependsOn?.questionId === step.questionId);
        return !hasDependents || dependentsSettled(step.questionId)(view);
      });
    }
    console.log(`  ${secondAnalyst}: ${c.decisions.length} decisions on "${c.title}"`);
  }

  // Summary from the final state.
  let proposals = 0;
  let finals = 0;
  let withdrawn = 0;
  for (const c of cases) {
    const view = await getView(c.caseId, analystToken);
    for (const q of view.questions) {
      if (q.proposal) proposals++;
      if (q.answer) finals++;
      if (q.proposal?.status === 'Withdrawn') withdrawn++;
    }
  }
  const liveView = await getView(cases[0].caseId, analystToken);
  const liveOpen = liveView.questions.filter((q) => q.proposal?.status === 'Proposed').length;

  console.log('Seed summary');
  console.log(`  cases:                   ${cases.length}`);
  console.log(`  proposals shown:         ${proposals}`);
  console.log(`  final answers:           ${finals}`);
  console.log(`  withdrawn proposals:     ${withdrawn}`);
  console.log(`  live demo open proposals: ${liveOpen}`);
  console.log('Log in as "A. Silva" (Reviewer) or "alice" (Submitter).');
}

main().catch((err) => {
  console.error(`Seeding failed: ${err.message}`);
  process.exit(1);
});

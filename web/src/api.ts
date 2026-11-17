export type Role = 'Submitter' | 'Reviewer';

export const ROLE_CLAIMS: Record<Role, string> = {
  Submitter: 'Case.Submit',
  Reviewer: 'Case.Review',
};

// Mirrors the API JSON: camelCase, enums as names, null properties omitted.
export type CaseStatus = 'Draft' | 'Ready' | 'Closed';
export type ProposalStatus = 'Proposed' | 'NeedsAnalyst' | 'Withdrawn' | 'Accepted' | 'Rejected';
export type ConfidenceBand = 'High' | 'Medium' | 'Low';
export type Applicability = 'Applicable' | 'NotApplicable' | 'ParentUnconfirmed';

export interface CaseItem {
  id: string;
  caseId: string;
  title: string;
  submittedBy: string;
  status: CaseStatus;
  aiLocked: boolean;
  lockedBy?: string;
  auditSeq: number;
  createdAt: string;
  _etag?: string;
}

export interface DocumentPage {
  n: number;
  text: string;
}

export interface CaseDocument {
  id: string;
  caseId: string;
  documentId: string;
  name: string;
  kind: string;
  pages: DocumentPage[];
  addedAt: string;
}

export interface FinalAnswer {
  questionId: string;
  value: string;
  setBy: string;
  setAt: string;
  reason?: string;
}

export interface Proposal {
  questionId: string;
  value?: string;
  confidence?: number;
  confidenceBand?: ConfidenceBand;
  quote?: string;
  documentId?: string;
  page?: number;
  agent: string;
  promptVersion: string;
  modelVersion: string;
  verifierPassed: boolean;
  status: ProposalStatus;
  dependsOnUnconfirmed: boolean;
  withdrawnReason?: string;
  needsAnalystReason?: string;
}

export interface Question {
  id: string;
  text: string;
  type: 'yes_no' | 'free_text';
  required: boolean;
  dependsOn?: { questionId: string; value: string };
}

export interface QuestionState {
  question: Question;
  applicability: Applicability;
  answer?: FinalAnswer;
  proposal?: Proposal;
}

export interface Gap {
  kind: 'MissingDocument' | 'NoEvidence';
  message: string;
  documentKind?: string;
  questionId?: string;
}

export interface CaseView {
  case: CaseItem;
  documents: CaseDocument[];
  questions: QuestionState[];
  gaps: Gap[];
  aiPending: boolean;
  promptVersion: string;
}

export interface CaseSummary {
  case: CaseItem;
  questions: number;
  answered: number;
  openProposals: number;
  needsAnalyst: number;
}

export interface Questionnaire {
  promptVersion: string;
  requiredDocuments: { kind: string; name: string }[];
  questions: Question[];
}

export interface AuditBasis {
  documentId: string;
  page: number;
  quote: string;
}

export interface AuditViewRow {
  questionId: string;
  questionText: string;
  proposedValue?: string;
  proposedBy?: string;
  promptVersion?: string;
  proposalStatus?: ProposalStatus;
  basis?: AuditBasis;
  confidence?: number;
  band?: ConfidenceBand;
  verifierPassed?: boolean;
  finalValue?: string;
  finalBy?: string;
  finalAt?: string;
  reason?: string;
  effect?: string;
}

export interface AuditEntry {
  id: string;
  seq: number;
  at: string;
  actor: { type: string; id: string };
  action: string;
  questionId?: string;
  before?: string;
  after?: string;
  reason?: string;
}

export interface AuditView {
  rows: AuditViewRow[];
  entries: AuditEntry[];
}

export interface Faults {
  aiBackendDown: boolean;
  caseAppDown: boolean;
}

export interface OpsStatus {
  bus: { deadLetterCount: number; pending: number };
  outboxCount: number;
  faults: Faults;
}

export interface Session {
  token: string;
  expiresAt: string;
  userId: string;
  role: Role;
}

const KEY = 'case-agents-session';

export function loadSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(KEY);
    if (!raw) return null;
    const session = JSON.parse(raw) as Session;
    // An expired dev token would only produce 401s; ask for a new login instead.
    return Date.parse(session.expiresAt) > Date.now() ? session : null;
  } catch {
    return null;
  }
}

export function saveSession(s: Session | null): void {
  if (s) sessionStorage.setItem(KEY, JSON.stringify(s));
  else sessionStorage.removeItem(KEY);
}

export class ApiError extends Error {
  status: number;
  code?: string;
  constructor(status: number, message: string, code?: string) {
    super(`${status}: ${message}`);
    this.status = status;
    this.code = code;
  }
}

// App registers this so an expired or rejected session sends the user back to the login screen.
let onUnauthorized: (() => void) | null = null;

export function setUnauthorizedHandler(handler: (() => void) | null): void {
  onUnauthorized = handler;
}

function sessionLost(): ApiError {
  saveSession(null);
  onUnauthorized?.();
  return new ApiError(401, 'Session expired, please sign in again');
}

// Endpoints that work without a token (dev only).
const ANONYMOUS = ['/dev/token', '/dev/faults'];

async function request<T>(method: string, path: string, body?: unknown, etag?: string): Promise<T> {
  const needsAuth = !ANONYMOUS.includes(path);
  const headers: Record<string, string> = {};
  const session = loadSession();
  if (session) headers['Authorization'] = `Bearer ${session.token}`;
  else if (needsAuth) throw sessionLost();
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (etag) headers['If-Match'] = etag;
  const res = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 401 && needsAuth) throw sessionLost();
  const text = await res.text().catch(() => '');
  if (!res.ok) {
    let message = res.statusText || 'Request failed';
    let code: string | undefined;
    if (text) {
      try {
        const json = JSON.parse(text) as { error?: string; title?: string; code?: string };
        message = json.error ?? json.title ?? text;
        code = json.code;
      } catch {
        message = text;
      }
    }
    throw new ApiError(res.status, message, code);
  }
  return (text ? JSON.parse(text) : undefined) as T;
}

export async function login(userId: string, role: Role): Promise<Session> {
  const r = await request<{ accessToken: string; expiresAt: string }>('POST', '/dev/token', {
    userId,
    roles: [ROLE_CLAIMS[role]],
  });
  return { token: r.accessToken, expiresAt: r.expiresAt, userId, role };
}

const enc = encodeURIComponent;

export const getQuestionnaire = () => request<Questionnaire>('GET', '/api/questionnaire');
export const getCases = () => request<CaseSummary[]>('GET', '/api/cases');
export const createCase = (title: string) => request<CaseItem>('POST', '/api/cases', { title });
export const getCase = (id: string) => request<CaseView>('GET', `/api/cases/${enc(id)}`);
export const addDocument = (
  id: string,
  etag: string | undefined,
  doc: { documentId: string; name: string; kind: string; pages: DocumentPage[] },
) => request<CaseItem>('POST', `/api/cases/${enc(id)}/documents`, doc, etag);
export const markReady = (id: string, etag?: string) =>
  request<CaseItem>('POST', `/api/cases/${enc(id)}/ready`, undefined, etag);
export const takeOver = (id: string, etag?: string) =>
  request<CaseItem>('POST', `/api/cases/${enc(id)}/takeover`, undefined, etag);
export const release = (id: string, etag?: string) =>
  request<CaseItem>('POST', `/api/cases/${enc(id)}/release`, undefined, etag);
export const answer = (
  id: string,
  questionId: string,
  etag: string | undefined,
  body: { action: 'accept' | 'edit' | 'reject'; value?: string; reason?: string },
) => request<CaseView>('POST', `/api/cases/${enc(id)}/answers/${enc(questionId)}`, body, etag);
export const getAudit = (id: string) => request<AuditView>('GET', `/api/cases/${enc(id)}/audit`);
export const getOps = () => request<OpsStatus>('GET', '/api/ops/status');
export const getFaults = () => request<Faults>('GET', '/dev/faults');
export const setFaults = (f: Partial<Faults>) => request<Faults>('POST', '/dev/faults', f);

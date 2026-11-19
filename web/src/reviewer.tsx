import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  answer,
  getAudit,
  getCase,
  getCases,
  release,
  takeOver,
  type AuditView,
  type CaseDocument,
  type CaseSummary,
  type CaseView,
  type ConfidenceBand,
  type QuestionState,
} from './api.ts';
import { auditProposedText, errorText, formatTime, isSetAside, notApplicableText, showDependsFlag, splitAtQuote } from './format.ts';
import { ErrorBanner } from './pages.tsx';

export function ReviewerListPage({ onOpen }: { onOpen: (caseId: string) => void }) {
  const [cases, setCases] = useState<CaseSummary[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getCases().then(setCases).catch((e) => setError(errorText(e)));
  }, []);

  return (
    <div className="card">
      <h2>Cases</h2>
      <ErrorBanner error={error} />
      {cases.length === 0 && <p className="muted">No cases yet.</p>}
      <ul className="list">
        {cases.map((s) => (
          <li key={s.case.caseId} className="row" data-testid="case-row" data-title={s.case.title} onClick={() => onOpen(s.case.caseId)}>
            <span className="row-title">{s.case.title}</span>
            <span className="badge" data-testid="case-status">
              {s.case.status}
            </span>
            <span className="muted">
              {s.answered}/{s.questions} answered
            </span>
            <span className="muted">{s.openProposals} open proposals</span>
            <span className="muted">{s.needsAnalyst} need analyst</span>
            {s.case.aiLocked && <span className="badge badge-lock">Locked</span>}
          </li>
        ))}
      </ul>
    </div>
  );
}

// Polls while the AI backend still has work for this case. aiPending covers queued events, a run in progress and
// the outbox. Right after a change the next event may not be queued yet, so a few extra polls follow every change.
const EXTRA_POLLS = 3;

export function CasePage({ caseId, onBack }: { caseId: string; onBack: () => void }) {
  const [view, setView] = useState<CaseView | null>(null);
  const [tab, setTab] = useState<'questions' | 'audit'>('questions');
  const [error, setError] = useState<string | null>(null);
  // Polls left after the last change of the case version, so polling stops on a quiet case.
  const [extraPolls, setExtraPolls] = useState(EXTRA_POLLS);
  const [seenEtag, setSeenEtag] = useState<string | undefined>(undefined);

  const load = useCallback(async () => {
    try {
      setView(await getCase(caseId));
    } catch (e) {
      setError(errorText(e));
    }
  }, [caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  const etagNow = view?.case._etag;
  if (etagNow !== seenEtag) {
    setSeenEtag(etagNow);
    setExtraPolls(EXTRA_POLLS);
  }

  // Poll every 2 s while the AI backend is still working on this case.
  useEffect(() => {
    if (!view || view.case.status !== 'Ready') return;
    if (!view.aiPending && extraPolls <= 0) return;
    const timer = setTimeout(() => {
      if (!view.aiPending) setExtraPolls((n) => n - 1);
      void load();
    }, 2000);
    return () => clearTimeout(timer);
  }, [view, extraPolls, load]);

  // Runs a write and returns true when it succeeded. On a conflict the case is reloaded and the analyst is told.
  async function run(action: () => Promise<CaseView | unknown>): Promise<boolean> {
    setError(null);
    try {
      const result = await action();
      if (result && typeof result === 'object' && 'questions' in result) setView(result as CaseView);
      else await load();
      return true;
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        setError('The case changed; reloaded.');
        await load();
      } else {
        setError(errorText(e));
      }
      return false;
    }
  }

  if (!view) {
    return (
      <div className="card">
        <button onClick={onBack}>Back</button>
        {error ? <ErrorBanner error={error} /> : <p className="muted">Loading...</p>}
      </div>
    );
  }

  const c = view.case;
  const etag = c._etag;

  return (
    <>
      <div className="card">
        <div className="list-header">
          <h2>{c.title}</h2>
          <button onClick={onBack}>Back to cases</button>
        </div>
        <div className="actions">
          <span className="badge" data-testid="case-status">
            {c.status}
          </span>
          {c.aiLocked ? (
            <>
              <span className="badge badge-lock" data-testid="locked-badge">
                Taken over by {c.lockedBy}: AI writes blocked
              </span>
              <button data-testid="release" onClick={() => void run(() => release(caseId, etag))}>
                Release
              </button>
            </>
          ) : (
            <button data-testid="takeover" onClick={() => void run(() => takeOver(caseId, etag))}>
              Take over
            </button>
          )}
        </div>
        <ErrorBanner error={error} />
        {view.aiPending && (
          <div className="notice" data-testid="ai-pending-banner">
            AI pending: the AI backend has not finished this case. You can keep working.
          </div>
        )}
        <div className="tabs">
          <button className={tab === 'questions' ? 'tab active' : 'tab'} data-testid="tab-questions" onClick={() => setTab('questions')}>
            Questions
          </button>
          <button className={tab === 'audit' ? 'tab active' : 'tab'} data-testid="tab-audit" onClick={() => setTab('audit')}>
            Audit
          </button>
        </div>
      </div>
      {tab === 'questions' ? (
        <>
          {view.gaps.length > 0 && (
            <div className="card">
              <h3>Gaps</h3>
              <ul>
                {view.gaps.map((g, i) => (
                  <li key={i} data-testid="gap-item">
                    {g.message}
                  </li>
                ))}
              </ul>
            </div>
          )}
          {view.questions.map((q) => (
            <QuestionCard
              key={q.question.id}
              state={q}
              parentFinal={view.questions.find((s) => s.question.id === q.question.dependsOn?.questionId)?.answer?.value}
              documents={view.documents}
              onAnswer={(body) => run(() => answer(caseId, q.question.id, etag, body))}
            />
          ))}
        </>
      ) : (
        <AuditTab caseId={caseId} documents={view.documents} refreshKey={c.auditSeq} />
      )}
    </>
  );
}

const bandClass: Record<ConfidenceBand, string> = { High: 'band-high', Medium: 'band-medium', Low: 'band-low' };

type AnswerBody = { action: 'accept' | 'edit' | 'reject'; value?: string; reason?: string };

function QuestionCard({
  state,
  parentFinal,
  documents,
  onAnswer,
}: {
  state: QuestionState;
  parentFinal: string | undefined;
  documents: CaseDocument[];
  onAnswer: (body: AnswerBody) => Promise<boolean>;
}) {
  const { question, applicability, answer: final, proposal } = state;
  const [mode, setMode] = useState<'none' | 'edit' | 'reject'>('none');
  const [value, setValue] = useState(proposal?.status === 'Proposed' ? (proposal.value ?? '') : '');
  const [reason, setReason] = useState('');
  const [showEvidence, setShowEvidence] = useState(false);

  // The form is cleared only after a successful write, so a 409 keeps the typed reason.
  async function send(body: AnswerBody) {
    if (!(await onAnswer(body))) return;
    setMode('none');
    setReason('');
  }

  const notApplicable = applicability === 'NotApplicable';
  const setAside = isSetAside(!!final, applicability);
  const dependsFlag = showDependsFlag(proposal?.dependsOnUnconfirmed, applicability);
  const canDecide = !notApplicable;
  const canReject = !!proposal && (proposal.status === 'Proposed' || proposal.status === 'NeedsAnalyst');

  return (
    <div className="card question" data-testid={`question-${question.id}`}>
      <h3>
        {question.id} <span className="q-text">{question.text}</span>
      </h3>
      {notApplicable && (
        <p className="muted" data-testid="not-applicable">
          {notApplicableText(question.dependsOn, parentFinal)}
        </p>
      )}
      {final && (
        <div className={setAside ? 'final final-set-aside' : 'final'} data-testid="final-value">
          {setAside && (
            <span className="badge badge-warn" data-testid="set-aside">
              Set aside: no longer applies
            </span>
          )}{' '}
          Final: <strong>{final.value}</strong> · set by {final.setBy} · {formatTime(final.setAt)}
          {final.reason ? ` · ${final.reason}` : ''}
        </div>
      )}
      {proposal && (
        <div className="proposal">
          {proposal.status === 'NeedsAnalyst' ? (
            <p data-testid="needs-analyst">Needs analyst: {proposal.needsAnalystReason ?? 'no usable answer'}</p>
          ) : (
            <p>
              Proposed: <strong data-testid="proposal-value">{proposal.value}</strong>
            </p>
          )}
          <div className="actions">
            {proposal.confidenceBand && (
              <span className={`badge ${bandClass[proposal.confidenceBand]}`} data-testid="confidence-badge">
                {proposal.confidenceBand}
              </span>
            )}
            {proposal.confidence !== undefined && <span className="muted">{Math.round(proposal.confidence * 100)}%</span>}
            {proposal.verifierPassed && (
              <span className="badge badge-ok" data-testid="verifier-passed">
                verifier passed
              </span>
            )}
            <span className="muted">
              {proposal.agent} agent · prompt {proposal.promptVersion}
            </span>
            <span className="badge" data-testid="proposal-status">
              {proposal.status}
            </span>
            {dependsFlag && (
              <span className="badge badge-warn" data-testid="depends-flag">
                Depends on {question.dependsOn?.questionId}, not confirmed yet
              </span>
            )}
          </div>
          {proposal.status === 'Withdrawn' && proposal.withdrawnReason && <p className="muted">Withdrawn: {proposal.withdrawnReason}</p>}
          {proposal.quote && proposal.documentId && (
            <button className="secondary" data-testid="show-evidence" onClick={() => setShowEvidence(!showEvidence)}>
              {showEvidence ? 'Hide evidence' : 'Show evidence'}
            </button>
          )}
          {showEvidence && proposal.documentId && proposal.page !== undefined && (
            <DocViewer documents={documents} documentId={proposal.documentId} page={proposal.page} quote={proposal.quote ?? ''} />
          )}
        </div>
      )}
      {canDecide && (
        <div className="actions">
          {proposal?.status === 'Proposed' && (
            <button data-testid="accept" onClick={() => void send({ action: 'accept' })}>
              Accept
            </button>
          )}
          <button className="secondary" data-testid="edit" onClick={() => setMode(mode === 'edit' ? 'none' : 'edit')}>
            Edit
          </button>
          {canReject && (
            <button className="secondary" data-testid="reject" onClick={() => setMode(mode === 'reject' ? 'none' : 'reject')}>
              Reject
            </button>
          )}
        </div>
      )}
      {mode === 'edit' && (
        <div className="stack">
          {question.type === 'yes_no' ? (
            <select data-testid="edit-value" value={value} onChange={(e) => setValue(e.target.value)}>
              <option value="">Choose...</option>
              <option value="Yes">Yes</option>
              <option value="No">No</option>
            </select>
          ) : (
            <input data-testid="edit-value" value={value} onChange={(e) => setValue(e.target.value)} />
          )}
          <input data-testid="edit-reason" placeholder="Reason (required)" value={reason} onChange={(e) => setReason(e.target.value)} />
          <button data-testid="edit-save" disabled={!reason.trim() || !value.trim()} onClick={() => void send({ action: 'edit', value, reason: reason.trim() })}>
            Save
          </button>
        </div>
      )}
      {mode === 'reject' && (
        <div className="stack">
          <input data-testid="reject-reason" placeholder="Reason (required)" value={reason} onChange={(e) => setReason(e.target.value)} />
          <button data-testid="reject-save" disabled={!reason.trim()} onClick={() => void send({ action: 'reject', reason: reason.trim() })}>
            Reject proposal
          </button>
        </div>
      )}
    </div>
  );
}

function DocViewer({ documents, documentId, page, quote }: { documents: CaseDocument[]; documentId: string; page: number; quote: string }) {
  const doc = documents.find((d) => d.documentId === documentId);
  const pageItem = doc?.pages.find((p) => p.n === page);
  const parts = pageItem ? splitAtQuote(pageItem.text, quote) : null;
  return (
    <div className="doc-viewer" data-testid="doc-viewer">
      <p className="muted">
        {doc?.name ?? documentId}, p. {page}
      </p>
      <div className="doc-page" data-testid="doc-page">
        {!pageItem && 'Page not found.'}
        {pageItem && !parts && pageItem.text}
        {pageItem && parts && (
          <>
            {parts[0]}
            <mark data-testid="quote-highlight">{parts[1]}</mark>
            {parts[2]}
          </>
        )}
      </div>
    </div>
  );
}

function AuditTab({ caseId, documents, refreshKey }: { caseId: string; documents: CaseDocument[]; refreshKey: number }) {
  const [audit, setAudit] = useState<AuditView | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getAudit(caseId).then(setAudit).catch((e) => setError(errorText(e)));
  }, [caseId, refreshKey]);

  const docName = (id: string) => documents.find((d) => d.documentId === id)?.name ?? id;

  return (
    <div className="card audit-card">
      <h3>Audit</h3>
      <p className="muted">Times in UTC</p>
      <ErrorBanner error={error} />
      {audit && (
        <>
          <table className="audit" data-testid="audit-table">
            <colgroup>
              <col className="col-question" />
              <col className="col-proposed" />
              <col className="col-basis" />
              <col className="col-confidence" />
              <col className="col-final" />
              <col className="col-reason" />
              <col className="col-effect" />
            </colgroup>
            <thead>
              <tr>
                <th>Question</th>
                <th>Proposed</th>
                <th>Basis</th>
                <th>Confidence</th>
                <th>Final</th>
                <th>Reason</th>
                <th>Effect</th>
              </tr>
            </thead>
            <tbody>
              {audit.rows.map((r) => (
                <tr key={r.questionId} data-testid={`audit-row-${r.questionId}`}>
                  <td>
                    <strong>{r.questionId}</strong> {r.questionText}
                  </td>
                  <td data-testid="audit-proposed">
                    {r.proposedBy && auditProposedText(r)}
                  </td>
                  <td data-testid="audit-basis">
                    {r.basis && (
                      <>
                        {docName(r.basis.documentId)}, p. {r.basis.page}, &quot;{r.basis.quote}&quot;
                      </>
                    )}
                  </td>
                  <td data-testid="audit-confidence">
                    {r.band}
                    {r.verifierPassed ? ' · verifier passed' : ''}
                  </td>
                  <td data-testid="audit-final">
                    {r.finalValue && (
                      <>
                        {r.finalValue} · {r.finalBy} · {r.finalAt ? formatTime(r.finalAt) : ''}
                      </>
                    )}
                  </td>
                  <td data-testid="audit-reason">{r.reason}</td>
                  <td data-testid="audit-effect">{r.effect}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <details>
            <summary>Raw entries (append-only)</summary>
            <ol className="raw">
              {audit.entries.map((e) => (
                <li key={e.id}>
                  {formatTime(e.at)} · {e.actor.type} {e.actor.id} · {e.action}
                  {e.questionId ? ` · ${e.questionId}` : ''}
                  {e.before || e.after ? ` · ${e.before ?? '-'} to ${e.after ?? '-'}` : ''}
                  {e.reason ? ` · ${e.reason}` : ''}
                </li>
              ))}
            </ol>
          </details>
        </>
      )}
    </div>
  );
}

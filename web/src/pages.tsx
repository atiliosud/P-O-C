import { useCallback, useEffect, useState, type FormEvent } from 'react';
import {
  addDocument,
  createCase,
  getCase,
  getCases,
  getQuestionnaire,
  login,
  markReady,
  type CaseSummary,
  type CaseView,
  type Questionnaire,
  type Role,
  type Session,
} from './api.ts';
import { errorText } from './format.ts';

export function ErrorBanner({ error }: { error: string | null }) {
  if (!error) return null;
  return (
    <div className="error" role="alert" data-testid="error-banner">
      {error}
    </div>
  );
}

export function LoginPage({ onLogin }: { onLogin: (s: Session) => void }) {
  const [userId, setUserId] = useState('A. Silva');
  const [role, setRole] = useState<Role>('Reviewer');
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      onLogin(await login(userId.trim(), role));
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <form onSubmit={submit} className="card">
      <h2>Dev login</h2>
      <ErrorBanner error={error} />
      <label>
        User
        <input data-testid="login-user" value={userId} onChange={(e) => setUserId(e.target.value)} required maxLength={64} />
      </label>
      <label>
        Role
        <select data-testid="login-role" value={role} onChange={(e) => setRole(e.target.value as Role)}>
          <option value="Submitter">Submitter (Case.Submit)</option>
          <option value="Reviewer">Reviewer (Case.Review)</option>
        </select>
      </label>
      <button type="submit" data-testid="login-submit" disabled={!userId.trim()}>
        Sign in
      </button>
    </form>
  );
}

export function SubmitterPage() {
  const [cases, setCases] = useState<CaseSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [title, setTitle] = useState('');
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setCases(await getCases());
    } catch (e) {
      setError(errorText(e));
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  async function create(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const created = await createCase(title.trim());
      setTitle('');
      setSelected(created.caseId);
      await refresh();
    } catch (err) {
      setError(errorText(err));
    }
  }

  return (
    <>
      <ErrorBanner error={error} />
      <form onSubmit={create} className="card">
        <h2>New case</h2>
        <label>
          Title
          <input data-testid="new-case-title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} required />
        </label>
        <button type="submit" data-testid="new-case-create" disabled={!title.trim()}>
          Create case
        </button>
      </form>
      <div className="card">
        <h2>My cases</h2>
        {cases.length === 0 && <p className="muted">No cases yet.</p>}
        <ul className="list">
          {cases.map((s) => (
            <li
              key={s.case.caseId}
              className="row"
              data-testid="case-row"
              data-title={s.case.title}
              onClick={() => setSelected(s.case.caseId)}
            >
              <span className="row-title">{s.case.title}</span>
              <span className="badge" data-testid="case-status">
                {s.case.status}
              </span>
            </li>
          ))}
        </ul>
      </div>
      {selected && <SubmitterCase key={selected} caseId={selected} onChanged={refresh} />}
    </>
  );
}

function SubmitterCase({ caseId, onChanged }: { caseId: string; onChanged: () => Promise<void> }) {
  const [view, setView] = useState<CaseView | null>(null);
  const [questionnaire, setQuestionnaire] = useState<Questionnaire | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [kind, setKind] = useState('other');
  const [pages, setPages] = useState('');

  const load = useCallback(async () => {
    try {
      setView(await getCase(caseId));
    } catch (e) {
      setError(errorText(e));
    }
  }, [caseId]);

  useEffect(() => {
    void load();
    getQuestionnaire().then(setQuestionnaire).catch((e) => setError(errorText(e)));
  }, [load]);

  async function add(e: FormEvent) {
    e.preventDefault();
    if (!view) return;
    setError(null);
    // A line that is exactly --- separates pages.
    const parts = pages.split(/^---\s*$/m).map((p) => p.trim()).filter((p) => p.length > 0);
    try {
      await addDocument(caseId, view.case._etag, {
        documentId: `doc-${view.documents.length + 1}`,
        name: name.trim(),
        kind,
        pages: parts.map((text, i) => ({ n: i + 1, text })),
      });
      setName('');
      setPages('');
      await load();
    } catch (err) {
      setError(errorText(err));
      await load();
    }
  }

  async function ready() {
    if (!view) return;
    setError(null);
    try {
      await markReady(caseId, view.case._etag);
      await load();
      await onChanged();
    } catch (err) {
      setError(errorText(err));
      await load();
    }
  }

  if (!view) return <div className="card">{error ? <ErrorBanner error={error} /> : <p className="muted">Loading...</p>}</div>;
  const isDraft = view.case.status === 'Draft';

  return (
    <div className="card">
      <h2>{view.case.title}</h2>
      <ErrorBanner error={error} />
      <p className="muted">
        Status: <span className="badge">{view.case.status}</span>
      </p>
      <h3>Documents</h3>
      {view.documents.length === 0 && <p className="muted">No documents yet.</p>}
      <ul className="list">
        {view.documents.map((d) => (
          <li key={d.documentId} className="row">
            <span className="row-title">{d.name}</span>
            <span className="muted">
              {d.kind}, {d.pages.length} page(s)
            </span>
          </li>
        ))}
      </ul>
      {isDraft && (
        <>
          <form onSubmit={add} className="stack">
            <h3>Add document</h3>
            <label>
              Name
              <input data-testid="doc-name" value={name} onChange={(e) => setName(e.target.value)} required />
            </label>
            <label>
              Kind
              <select data-testid="doc-kind" value={kind} onChange={(e) => setKind(e.target.value)}>
                {questionnaire?.requiredDocuments.map((d) => (
                  <option key={d.kind} value={d.kind}>
                    {d.kind}
                  </option>
                ))}
                <option value="other">other</option>
              </select>
            </label>
            <label>
              Pages (a line with --- separates pages)
              <textarea data-testid="doc-pages" rows={6} value={pages} onChange={(e) => setPages(e.target.value)} />
            </label>
            <button type="submit" data-testid="doc-add" disabled={!name.trim() || !pages.trim()}>
              Add document
            </button>
          </form>
          <button data-testid="case-ready" onClick={() => void ready()}>
            Mark ready
          </button>
        </>
      )}
    </div>
  );
}

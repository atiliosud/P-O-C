import { useEffect, useState } from 'react';
import { getFaults, getOps, loadSession, saveSession, setFaults, setUnauthorizedHandler, type Faults, type OpsStatus, type Session } from './api.ts';
import { LoginPage, SubmitterPage } from './pages.tsx';
import { CasePage, ReviewerListPage } from './reviewer.tsx';

function Header({ session, onLogout }: { session: Session; onLogout: () => void }) {
  const [ops, setOps] = useState<OpsStatus | null>(null);
  const [faults, setFaultState] = useState<Faults>({ aiBackendDown: false, caseAppDown: false });
  const isReviewer = session.role === 'Reviewer';

  useEffect(() => {
    let cancelled = false;
    const poll = () => {
      getFaults().then((f) => !cancelled && setFaultState(f)).catch(() => undefined);
      if (isReviewer) getOps().then((o) => !cancelled && setOps(o)).catch(() => undefined);
    };
    poll();
    const timer = setInterval(poll, 5000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [isReviewer]);

  async function toggle(change: Partial<Faults>) {
    try {
      setFaultState(await setFaults(change));
    } catch {
      // The next poll shows the real state.
    }
  }

  return (
    <header>
      <h1>Compliance questionnaire</h1>
      <div className="header-right">
        <span className="who" data-testid="whoami">
          {session.userId} ({session.role})
        </span>
        {isReviewer && (
          <>
            <span className={`badge ${ops && ops.bus.deadLetterCount > 0 ? 'badge-bad' : ''}`}>
              Dead letters: <span data-testid="ops-deadletters">{ops?.bus.deadLetterCount ?? 0}</span>
            </span>
            <span className="badge">
              Outbox: <span data-testid="ops-outbox">{ops?.outboxCount ?? 0}</span>
            </span>
          </>
        )}
        <button data-testid="nav-logout" onClick={onLogout}>
          Log out
        </button>
        <fieldset className="faults">
          <legend>Dev faults</legend>
          <label className="check">
            <input
              type="checkbox"
              data-testid="fault-ai-down"
              checked={faults.aiBackendDown}
              onChange={(e) => void toggle({ aiBackendDown: e.target.checked })}
            />
            AI backend down
          </label>
          <label className="check">
            <input
              type="checkbox"
              data-testid="fault-app-down"
              checked={faults.caseAppDown}
              onChange={(e) => void toggle({ caseAppDown: e.target.checked })}
            />
            Case app down
          </label>
        </fieldset>
      </div>
    </header>
  );
}

export default function App() {
  const [session, setSession] = useState<Session | null>(loadSession);
  const [caseId, setCaseId] = useState<string | null>(null);

  // The API client clears the stored session on a missing/expired token or a 401; drop to the login screen.
  useEffect(() => {
    setUnauthorizedHandler(() => setSession(null));
    return () => setUnauthorizedHandler(null);
  }, []);

  function signIn(s: Session) {
    saveSession(s);
    setSession(s);
    setCaseId(null);
  }

  function signOut() {
    saveSession(null);
    setSession(null);
  }

  return (
    <div className="app">
      {session && <Header session={session} onLogout={signOut} />}
      {!session && <h1>Compliance questionnaire</h1>}
      <main>
        {!session && <LoginPage onLogin={signIn} />}
        {session?.role === 'Submitter' && <SubmitterPage />}
        {session?.role === 'Reviewer' && !caseId && <ReviewerListPage onOpen={setCaseId} />}
        {session?.role === 'Reviewer' && caseId && (
          <CasePage key={caseId} caseId={caseId} onBack={() => setCaseId(null)} />
        )}
      </main>
    </div>
  );
}

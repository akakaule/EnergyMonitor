import { useCallback, useEffect, useState } from 'react';
import { api, errorText, type Alert, type Status } from './api';
import { dateTime, kWh, shortDate } from './format';

export interface Loaded<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  reload: () => void;
}

/** Loads data when the dependency key changes, and again on demand. */
export function useLoad<T>(load: () => Promise<T>, key: string): Loaded<T> {
  const [state, setState] = useState<{ data: T | null; error: string | null; loading: boolean }>({
    data: null, error: null, loading: true,
  });
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let current = true;
    load().then(
      (data) => current && setState({ data, error: null, loading: false }),
      (error: unknown) => current && setState((s) => ({ data: s.data, error: errorText(error), loading: false })),
    );
    return () => {
      current = false;
    };
    // `load` is recreated on every render; `key` says when its inputs really changed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, tick]);

  const reload = useCallback(() => setTick((t) => t + 1), []);
  return { ...state, reload };
}

const STATUS_ICON: Record<Status, string> = { Normal: '✓', Observer: '◔', Alarm: '!', DataMissing: '?' };

/** Status is always an icon and a word, never colour alone. */
export function StatusBadge({ status, label }: { status: Status; label: string }) {
  return (
    <span className={`badge badge-${status.toLowerCase()}`}>
      <span className="badge-icon" aria-hidden="true">{STATUS_ICON[status]}</span>
      {label}
    </span>
  );
}

export function ErrorNote({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div className="note note-error" role="alert">
      <span>{message}</span>
      {onRetry && <button type="button" className="link" onClick={onRetry}>Prøv igen</button>}
    </div>
  );
}

function alertStatus(alert: Alert): Status {
  if (alert.ruleType === 'MissingData') return 'DataMissing';
  return alert.severity === 'Alarm' ? 'Alarm' : 'Observer';
}

export function AlertList({ alerts, onChanged, showProperty }: { alerts: Alert[]; onChanged: () => void; showProperty?: boolean }) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const acknowledge = async (id: string) => {
    setBusy(id);
    setError(null);
    try {
      await api.acknowledge(id);
      onChanged();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(null);
    }
  };

  if (alerts.length === 0) return <p className="empty">Ingen alarmer.</p>;
  return (
    <>
      {error && <ErrorNote message={error} />}
      <ul className="alerts">
        {alerts.map((a) => (
          <li key={a.id} className={a.state === 'Closed' ? 'alert alert-closed' : `alert alert-${alertStatus(a).toLowerCase()}`}>
            <div className="alert-head">
              <StatusBadge status={a.state === 'Closed' ? 'Normal' : alertStatus(a)} label={a.state === 'Closed' ? `${a.label} · lukket` : a.label} />
              <span className="alert-when">
                {showProperty ? `${a.propertyName} · ` : ''}
                {a.firstDate === a.lastDate ? shortDate(a.firstDate) : `${shortDate(a.firstDate)}–${shortDate(a.lastDate)}`}
              </span>
            </div>
            <p className="alert-text">{a.explanation}</p>
            {a.observed !== null && a.baseline !== null && (
              <p className="alert-basis">
                Målt {kWh(a.observed)} · median {kWh(a.baseline)}
                {a.threshold !== null ? ` · grænse ${kWh(a.threshold)}` : ''}
              </p>
            )}
            <div className="alert-foot">
              {a.acknowledgedAtUtc ? (
                <span className="muted">Kvitteret {dateTime(a.acknowledgedAtUtc)}</span>
              ) : (
                a.state === 'Open' && (
                  <button type="button" className="button button-small" disabled={busy === a.id} onClick={() => acknowledge(a.id)}>
                    Kvittér
                  </button>
                )
              )}
            </div>
          </li>
        ))}
      </ul>
    </>
  );
}

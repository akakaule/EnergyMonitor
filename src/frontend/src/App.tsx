import { useEffect, useState } from 'react';
import { api, ApiError, type Me } from './api';
import { Overview } from './Overview';
import { PropertyDetail } from './PropertyDetail';
import { Settings } from './Settings';

type Route = { page: 'overview' } | { page: 'property'; id: string } | { page: 'settings' };

export function parseRoute(hash: string): Route {
  const path = hash.replace(/^#\/?/, '');
  const property = /^ejendom\/([a-z0-9-]+)$/.exec(path);
  if (property) return { page: 'property', id: property[1] };
  if (path === 'indstillinger') return { page: 'settings' };
  return { page: 'overview' };
}

function useRoute(): Route {
  const [route, setRoute] = useState(() => parseRoute(window.location.hash));
  useEffect(() => {
    const onChange = () => {
      setRoute(parseRoute(window.location.hash));
      window.scrollTo(0, 0);
    };
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return route;
}

export function App() {
  const route = useRoute();
  const [me, setMe] = useState<Me | null>(null);
  const [denied, setDenied] = useState<string | null>(null);

  useEffect(() => {
    api.me().then(setMe, (error: unknown) => {
      if (error instanceof ApiError && error.status === 401) return; // on the way to sign-in
      setDenied(
        error instanceof ApiError && error.status === 403
          ? 'Din konto har ikke adgang til Energy Monitor.'
          : 'Der er ingen forbindelse til serveren. Prøv igen om lidt.',
      );
    });
  }, []);

  return (
    <div className="app">
      <header className="top">
        <a className="brand" href="#/">Energy Monitor</a>
        {me && (
          <nav aria-label="Hovedmenu">
            <a href="#/" aria-current={route.page === 'overview' ? 'page' : undefined}>Oversigt</a>
            <a href="#/indstillinger" aria-current={route.page === 'settings' ? 'page' : undefined}>Indstillinger</a>
          </nav>
        )}
      </header>
      <main>
        {denied && <p className="note note-error" role="alert">{denied}</p>}
        {me && route.page === 'overview' && <Overview />}
        {me && route.page === 'property' && <PropertyDetail key={route.id} id={route.id} />}
        {me && route.page === 'settings' && <Settings me={me} />}
        {!me && !denied && <p className="muted">Henter…</p>}
      </main>
    </div>
  );
}

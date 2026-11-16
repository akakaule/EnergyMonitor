import { useEffect, useState } from 'react';
import { api, ApiError, type Features, type Me } from './api';
import { HeatPump } from './HeatPump';
import { Overview } from './Overview';
import { PropertyDetail } from './PropertyDetail';
import { Settings } from './Settings';

type Route = { page: 'overview' } | { page: 'property'; id: string } | { page: 'heatpump' } | { page: 'settings'; section?: string };

export function parseRoute(hash: string): Route {
  const path = hash.replace(/^#\/?/, '');
  const property = /^ejendom\/([a-z0-9-]+)$/.exec(path);
  if (property) return { page: 'property', id: property[1] };
  if (/^varmepumpe\/?$/.test(path)) return { page: 'heatpump' };
  // Settings shows one section at a time: "indstillinger/notifikationer", "indstillinger/ejendom/hus".
  const settings = /^indstillinger(?:\/([a-z0-9-]+(?:\/[a-z0-9-]+)?))?\/?$/.exec(path);
  if (settings) return settings[1] ? { page: 'settings', section: settings[1] } : { page: 'settings' };
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
  const [features, setFeatures] = useState<Features | null>(null);

  useEffect(() => {
    api.me().then((signedIn) => {
      setMe(signedIn);
      // Without the list of features the menu simply leaves out the optional pages.
      api.features().then(setFeatures, () => undefined);
    }, (error: unknown) => {
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
        <a className="brand" href="#/" aria-label="Energy Monitor">
          <svg className="brand-mark" viewBox="0 0 100 100" aria-hidden="true">
            <rect width="100" height="100" rx="24" />
            <polygon points="56,14 27,56 47,56 41,86 73,42 52,42" />
          </svg>
          <span className="brand-name">Energy Monitor</span>
        </a>
        {me && (
          <nav aria-label="Hovedmenu">
            <a href="#/" aria-current={route.page === 'overview' ? 'page' : undefined}>Oversigt</a>
            {features?.heatpump && (
              <a href="#/varmepumpe" aria-current={route.page === 'heatpump' ? 'page' : undefined}>Varmepumpe</a>
            )}
            <a href="#/indstillinger" aria-current={route.page === 'settings' ? 'page' : undefined}>Indstillinger</a>
          </nav>
        )}
      </header>
      <main>
        {denied && <p className="note note-error" role="alert">{denied}</p>}
        {me && route.page === 'overview' && <Overview />}
        {me && route.page === 'property' && <PropertyDetail key={route.id} id={route.id} />}
        {me && route.page === 'heatpump' && <HeatPump me={me} />}
        {me && route.page === 'settings' && <Settings me={me} section={route.section} />}
        {!me && !denied && <p className="muted">Henter…</p>}
      </main>
    </div>
  );
}

import '@fontsource-variable/vazirmatn';
import './styles/tokens.css';
import './styles/base.css';

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { BridgeContext } from './app/bridgeContext';
import { createBridge } from './bridge';

const bridge = createBridge();

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // The bridge is local: no focus refetching, one quiet retry.
      refetchOnWindowFocus: false,
      retry: 1,
      retryDelay: 400,
      staleTime: 30_000,
    },
  },
});

// Tests and the owner can reach the mock's simulation controls from the console.
if (bridge.mock) (window as unknown as { deviceDeckMock: unknown }).deviceDeckMock = bridge.mock;

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BridgeContext.Provider value={bridge}>
      <QueryClientProvider client={queryClient}>
        <App />
      </QueryClientProvider>
    </BridgeContext.Provider>
  </StrictMode>,
);

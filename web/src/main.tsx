import '@fontsource-variable/vazirmatn';
import './styles/tokens.css';
import './styles/base.css';

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { BridgeContext } from './app/bridgeContext';
import { createBridge } from './bridge';
import { installNativeDrop, NativeDragPlayer } from './components/nativeDrop';

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

// Files dropped from File Explorer: the host plays them in the page. Tests drive the same player in the mock.
installNativeDrop();
if (bridge.mock) {
  const answers: boolean[] = [];
  const player = new NativeDragPlayer((accept) => answers.push(accept));
  (window as unknown as { deviceDeckNativeDrop: unknown }).deviceDeckNativeDrop = { play: player.play.bind(player), answers };
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BridgeContext.Provider value={bridge}>
      <QueryClientProvider client={queryClient}>
        <App />
      </QueryClientProvider>
    </BridgeContext.Provider>
  </StrictMode>,
);

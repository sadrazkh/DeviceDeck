import '@fontsource-variable/vazirmatn';
import '../../../styles/tokens.css';
import '../../../styles/base.css';

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BridgeContext } from '../../../app/bridgeContext';
import { createBridge } from '../../../bridge';
import { MirrorApp } from './MirrorApp';

// The mirror window's page (mirror.html): one phone's screen, opened by mirror.open.
const bridge = createBridge();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BridgeContext.Provider value={bridge}>
      <MirrorApp />
    </BridgeContext.Provider>
  </StrictMode>,
);

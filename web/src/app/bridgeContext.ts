import { createContext, useContext } from 'react';
import type { Bridge } from '../bridge';

export const BridgeContext = createContext<Bridge | null>(null);

export function useBridge(): Bridge {
  const bridge = useContext(BridgeContext);
  if (!bridge) throw new Error('useBridge needs a <BridgeContext.Provider>.');
  return bridge;
}

export function useClient() {
  return useBridge().client;
}

/** The mock backend's simulation controls; null inside the host. */
export function useMock() {
  return useBridge().mock;
}

import { create } from 'zustand';

export type ToastTone = 'info' | 'success' | 'warning' | 'danger';

export interface ToastAction {
  label: string;
  run(): void;
}

export interface Toast {
  id: number;
  tone: ToastTone;
  title: string;
  body?: string;
  action?: ToastAction;
  /** ms before it goes away by itself; 0 keeps it until dismissed. */
  duration: number;
}

interface ToastState {
  toasts: Toast[];
  push(toast: Omit<Toast, 'id' | 'duration'> & { duration?: number }): number;
  dismiss(id: number): void;
  clear(): void;
}

let nextId = 1;
const MAX_VISIBLE = 4;

export const useToasts = create<ToastState>((set) => ({
  toasts: [],
  push: (toast) => {
    const id = nextId++;
    const duration = toast.duration ?? (toast.tone === 'danger' ? 8000 : 5000);
    set((s) => ({ toasts: [...s.toasts, { ...toast, id, duration }].slice(-MAX_VISIBLE) }));
    return id;
  },
  dismiss: (id) => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),
  clear: () => set({ toasts: [] }),
}));

export const toast = (t: Parameters<ToastState['push']>[0]) => useToasts.getState().push(t);

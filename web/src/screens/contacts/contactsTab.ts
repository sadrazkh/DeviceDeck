import { create } from 'zustand';

export type ContactsTab = 'contacts' | 'messages' | 'whatsapp';

/** Lets another screen open Contacts and messages on one tab (App documents' WhatsApp guide, round 4). */
export const useContactsTab = create<{ requested: ContactsTab | null; request(tab: ContactsTab | null): void }>((set) => ({
  requested: null,
  request: (requested) => set({ requested }),
}));

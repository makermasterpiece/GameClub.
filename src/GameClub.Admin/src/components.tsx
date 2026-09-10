import { createContext, useContext, useEffect, useId, useRef, useState } from 'react';
import type { ReactNode } from 'react';

export const FeedbackContext = createContext<{ text: string; error: boolean } | null>(null);

export function Modal({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const feedback = useContext(FeedbackContext);
  useEffect(() => { ref.current?.showModal(); return () => ref.current?.close(); }, []);
  return <dialog ref={ref} className="modal" aria-labelledby={titleId} onCancel={event => { event.preventDefault(); onClose(); }}>
    <header><h2 id={titleId}>{title}</h2><button className="icon-button" aria-label="Закрыть" onClick={onClose}>×</button></header>
    {feedback?.error && <p className="notice error" role="alert">{feedback.text}</p>}{children}
  </dialog>;
}
export function MonitorIcon() {
  return <svg className="monitor-icon" width="44" height="38" viewBox="0 0 44 38" fill="none" aria-hidden="true"><rect x="3" y="3" width="38" height="25" rx="3" stroke="currentColor" strokeWidth="2" /><path d="M16 35h12M22 28v7" stroke="currentColor" strokeWidth="2" strokeLinecap="round" /></svg>;
}
export function Empty({ children }: { children: ReactNode }) { return <div className="empty">{children}</div>; }
export function useTick() {
  const [tick, setTick] = useState(0);
  useEffect(() => { const timer = setInterval(() => setTick(value => value + 1), 1000); return () => clearInterval(timer); }, []);
  return tick;
}
export type RunAction = (operation: () => Promise<unknown>, success: string) => Promise<boolean>;

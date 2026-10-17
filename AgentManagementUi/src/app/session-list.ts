import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { SessionStore } from './session-store';

@Component({
  selector: 'app-session-list',
  imports: [DatePipe],
  template: `
    <header>
      <h2>Sessions</h2>
      <button class="ghost" (click)="store.refresh()" [disabled]="store.loading()" title="Refresh">↻</button>
    </header>
    <button class="primary new" (click)="store.create()">+ New session</button>

    <ul>
      @for (s of store.sessions(); track s.sessionId) {
        <li [class.selected]="s.sessionId === store.selectedId()" (click)="store.selectedId.set(s.sessionId)">
          <div class="id">{{ s.sessionId }}</div>
          <div class="meta">
            @if (s.runtimeVersion) {
              <span>v{{ s.runtimeVersion }}</span>
              <span title="Estimated expiry (idle timeout / max lifetime)">expires {{ s.expiresAt | date: 'HH:mm' }}</span>
            } @else {
              <span class="badge">new · not started</span>
            }
          </div>
          <button class="ghost stop" title="Stop session" (click)="$event.stopPropagation(); store.stop(s.sessionId)">✕</button>
        </li>
      } @empty {
        <li class="empty">No active sessions</li>
      }
    </ul>
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 12px; padding: 16px; overflow: auto; }
    header { display: flex; align-items: center; justify-content: space-between; }
    h2 { margin: 0; font-size: 15px; }
    ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
    li { position: relative; padding: 8px 32px 8px 10px; border-radius: 8px; border: 1px solid var(--border); cursor: pointer; }
    li:hover { background: var(--hover); }
    li.selected { border-color: var(--accent); background: var(--accent-soft); }
    li.empty { cursor: default; color: var(--muted); border-style: dashed; }
    .id { font-family: var(--mono); font-size: 12px; word-break: break-all; }
    .meta { display: flex; gap: 8px; margin-top: 4px; font-size: 11px; color: var(--muted); }
    .badge { color: var(--accent); }
    .stop { position: absolute; top: 6px; right: 4px; font-size: 12px; }
  `,
})
export class SessionList {
  protected readonly store = inject(SessionStore);
}

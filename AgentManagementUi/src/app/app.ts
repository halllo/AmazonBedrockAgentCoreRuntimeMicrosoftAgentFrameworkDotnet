import { Component, OnInit, inject } from '@angular/core';
import { ChatPanel } from './chat-panel';
import { EventLog } from './event-log';
import { SessionList } from './session-list';
import { SessionStore } from './session-store';

@Component({
  selector: 'app-root',
  imports: [SessionList, ChatPanel, EventLog],
  template: `
    <header class="top">
      <h1>Agent Management</h1>
      @if (store.error(); as error) {
        <span class="error">{{ error }}</span>
      }
    </header>
    <main>
      <app-session-list />
      <app-chat-panel />
      <app-event-log />
    </main>
  `,
  styles: `
    :host { display: flex; flex-direction: column; height: 100vh; }
    .top { display: flex; align-items: center; gap: 16px; padding: 10px 16px; border-bottom: 1px solid var(--border); background: var(--surface); }
    h1 { margin: 0; font-size: 16px; }
    .error { color: var(--danger); font-size: 13px; }
    main { flex: 1; min-height: 0; display: grid; grid-template-columns: 280px minmax(0, 1fr) 400px; }
  `,
})
export class App implements OnInit {
  protected readonly store = inject(SessionStore);

  ngOnInit(): void {
    void this.store.refresh();
  }
}

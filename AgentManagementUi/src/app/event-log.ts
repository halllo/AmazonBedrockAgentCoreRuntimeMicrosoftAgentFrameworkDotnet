import { DatePipe, JsonPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { SessionStore } from './session-store';

const deltaTypes = new Set(['TEXT_MESSAGE_CONTENT', 'TEXT_MESSAGE_CHUNK', 'TOOL_CALL_ARGS']);

@Component({
  selector: 'app-event-log',
  imports: [DatePipe, JsonPipe],
  template: `
    <header>
      <h2>AG-UI events <span class="count">{{ store.current().events.length }}</span></h2>
      <button class="ghost" (click)="store.clearEvents()" [disabled]="!store.current().events.length">Clear</button>
    </header>
    <label class="toggle">
      <input type="checkbox" [checked]="showDeltas()" (change)="showDeltas.set(!showDeltas())" />
      Show content deltas
    </label>

    <ol>
      @for (entry of visible(); track entry.seq) {
        <li>
          <details>
            <summary>
              <span class="type" [attr.data-kind]="kind(entry.event.type)">{{ entry.event.type }}</span>
              <span class="preview">{{ preview(entry.event) }}</span>
              <time>{{ entry.receivedAt | date: 'HH:mm:ss.SSS' }}</time>
            </summary>
            <pre>{{ entry.event | json }}</pre>
          </details>
        </li>
      } @empty {
        <li class="empty">Events of the selected session's runs appear here.</li>
      }
    </ol>
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 8px; padding: 16px; overflow: auto; }
    header { display: flex; align-items: center; justify-content: space-between; }
    h2 { margin: 0; font-size: 15px; }
    .count { font-weight: normal; color: var(--muted); font-size: 12px; }
    .toggle { font-size: 12px; color: var(--muted); display: flex; gap: 6px; align-items: center; }
    ol { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; font-family: var(--mono); font-size: 11.5px; }
    summary { display: flex; gap: 8px; align-items: baseline; cursor: pointer; padding: 3px 4px; border-radius: 4px; }
    summary:hover { background: var(--hover); }
    .type { font-weight: 600; white-space: nowrap; }
    .type[data-kind='run'] { color: var(--accent); }
    .type[data-kind='text'] { color: var(--ok); }
    .type[data-kind='tool'] { color: var(--warn); }
    .type[data-kind='error'] { color: var(--danger); }
    .preview { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); }
    time { color: var(--muted); white-space: nowrap; }
    pre { margin: 2px 0 8px 12px; padding: 8px; background: var(--surface-2); border-radius: 6px; overflow: auto; white-space: pre-wrap; word-break: break-all; }
    .empty { color: var(--muted); font-family: inherit; font-size: 13px; }
  `,
})
export class EventLog {
  protected readonly store = inject(SessionStore);
  protected readonly showDeltas = signal(true);
  protected readonly visible = computed(() => {
    const events = this.store.current().events;
    return this.showDeltas() ? events : events.filter((e) => !deltaTypes.has(e.event.type));
  });

  protected kind(type: string): string {
    if (type.startsWith('RUN_ERROR')) return 'error';
    if (type.startsWith('RUN_') || type.startsWith('STEP_')) return 'run';
    if (type.startsWith('TEXT_')) return 'text';
    if (type.startsWith('TOOL_')) return 'tool';
    return 'other';
  }

  protected preview(event: Record<string, unknown>): string {
    const value = event['delta'] ?? event['content'] ?? event['toolCallName'] ?? event['message'] ?? event['runId'];
    return value === undefined ? '' : String(value);
  }
}

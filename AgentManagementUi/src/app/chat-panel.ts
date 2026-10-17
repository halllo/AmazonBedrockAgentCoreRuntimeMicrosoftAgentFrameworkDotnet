import { Component, ElementRef, effect, inject, signal, viewChild } from '@angular/core';
import { SessionStore } from './session-store';

@Component({
  selector: 'app-chat-panel',
  template: `
    @if (store.selectedId(); as id) {
      <header>
        <h2>Chat</h2>
        <code>{{ id }}</code>
      </header>

      <div class="messages" #scroller>
        @for (m of store.current().messages; track m.id) {
          @switch (m.role) {
            @case ('tool') {
              <div class="tool">
                <div>🔧 <strong>{{ m.toolName ?? 'tool' }}</strong>({{ m.toolArgs === 'null' ? '' : m.toolArgs }})</div>
                @if (m.toolResult !== undefined) {
                  <div class="result">→ {{ m.toolResult }}</div>
                }
              </div>
            }
            @default {
              <div class="bubble" [class]="m.role">{{ m.text }}</div>
            }
          }
        } @empty {
          <p class="hint">
            Send a prompt to talk to the agent. The agent keeps the conversation per session; this page only
            shows messages sent since it was loaded.
          </p>
        }
        @if (store.running()) {
          <div class="typing">agent is responding…</div>
        }
      </div>

      <form (submit)="$event.preventDefault(); submit()">
        <textarea
          rows="2"
          placeholder="Ask the agent… (Enter to send, Shift+Enter for a new line)"
          [value]="prompt()"
          (input)="prompt.set($any($event.target).value)"
          (keydown.enter)="onEnter($any($event))"
        ></textarea>
        @if (store.running()) {
          <button type="button" (click)="store.cancel()">Stop</button>
        } @else {
          <button class="primary" type="submit" [disabled]="!prompt().trim()">Send</button>
        }
      </form>
    } @else {
      <p class="hint center">Select a session or start a new one.</p>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; min-height: 0; border-left: 1px solid var(--border); border-right: 1px solid var(--border); }
    header { display: flex; align-items: baseline; gap: 12px; padding: 16px; border-bottom: 1px solid var(--border); }
    h2 { margin: 0; font-size: 15px; }
    code { font-size: 12px; color: var(--muted); }
    .messages { flex: 1; overflow: auto; padding: 16px; display: flex; flex-direction: column; gap: 10px; }
    .bubble { max-width: 80%; padding: 10px 14px; border-radius: 14px; white-space: pre-wrap; line-height: 1.45; }
    .bubble.user { align-self: flex-end; background: var(--accent); color: var(--on-accent); border-bottom-right-radius: 4px; }
    .bubble.assistant { align-self: flex-start; background: var(--surface-2); border-bottom-left-radius: 4px; }
    .bubble.error { align-self: stretch; background: var(--danger-soft); color: var(--danger); font-size: 13px; }
    .tool { align-self: flex-start; font-family: var(--mono); font-size: 12px; padding: 6px 10px; border: 1px dashed var(--border); border-radius: 8px; color: var(--muted); }
    .tool .result { margin-top: 2px; word-break: break-all; }
    .typing { color: var(--muted); font-size: 13px; font-style: italic; }
    .hint { color: var(--muted); font-size: 14px; }
    .center { margin: auto; }
    form { display: flex; gap: 8px; padding: 12px 16px; border-top: 1px solid var(--border); }
    textarea { flex: 1; resize: none; font: inherit; padding: 8px 10px; border-radius: 8px; border: 1px solid var(--border); background: var(--surface); color: inherit; }
    textarea:focus { outline: 2px solid var(--accent-soft); border-color: var(--accent); }
  `,
})
export class ChatPanel {
  protected readonly store = inject(SessionStore);
  protected readonly prompt = signal('');
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');

  constructor() {
    // Keep the newest message in view while the answer streams in.
    effect(() => {
      this.store.current();
      const el = this.scroller()?.nativeElement;
      if (el) queueMicrotask(() => (el.scrollTop = el.scrollHeight));
    });
  }

  protected onEnter(event: KeyboardEvent): void {
    if (event.shiftKey) return;
    event.preventDefault();
    this.submit();
  }

  protected submit(): void {
    const text = this.prompt().trim();
    if (!text || this.store.running()) return;
    this.prompt.set('');
    void this.store.send(text);
  }
}

import { Injectable, computed, inject, signal } from '@angular/core';
import { AgentApi, AguiEvent, SessionInfo } from './agent-api';

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant' | 'tool' | 'error';
  text: string;
  /** Tool calls only. */
  toolName?: string;
  toolArgs?: string;
  toolResult?: string;
}

export interface EventEntry {
  seq: number;
  receivedAt: Date;
  event: AguiEvent;
}

interface Conversation {
  messages: ChatMessage[];
  events: EventEntry[];
}

const emptyConversation: Conversation = { messages: [], events: [] };

/**
 * UI state. Chat transcripts and AG-UI events live in memory only; the agent keeps its own
 * history per session, so a page reload loses the transcript but not the agent's context.
 */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly api = inject(AgentApi);

  /** Sessions tracked by AgentManagement (only after their first invocation). */
  private readonly serverSessions = signal<SessionInfo[]>([]);
  /** Created here but not yet invoked, so not yet known to GET /sessions. */
  private readonly newSessionIds = signal<string[]>([]);
  private readonly conversations = signal<Record<string, Conversation>>({});
  private abort?: AbortController;
  private seq = 0;

  readonly sessions = computed(() => {
    const known = this.serverSessions();
    const pending = this.newSessionIds()
      .filter((id) => !known.some((s) => s.sessionId === id))
      .map((sessionId) => ({ sessionId, runtimeVersion: '', startedAt: '', lastActivityAt: '', expiresAt: '' }));
    return [...known, ...pending];
  });
  readonly selectedId = signal<string | null>(null);
  readonly running = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly current = computed(() => {
    const id = this.selectedId();
    return (id && this.conversations()[id]) || emptyConversation;
  });

  async refresh(): Promise<void> {
    this.loading.set(true);
    try {
      this.serverSessions.set(await this.api.listSessions());
      this.error.set(null);
    } catch (e) {
      this.error.set(describe(e));
    } finally {
      this.loading.set(false);
    }
  }

  async create(): Promise<void> {
    try {
      const { sessionId } = await this.api.createSession();
      this.newSessionIds.update((ids) => [...ids, sessionId]);
      this.selectedId.set(sessionId);
      this.error.set(null);
    } catch (e) {
      this.error.set(describe(e));
    }
  }

  async stop(sessionId: string): Promise<void> {
    try {
      await this.api.stopSession(sessionId);
    } catch (e) {
      // 404: AgentCore had already ended it (or it was never invoked) — drop it from the list anyway.
      if ((e as { status?: number }).status !== 404) {
        this.error.set(describe(e));
        return;
      }
    }
    this.newSessionIds.update((ids) => ids.filter((id) => id !== sessionId));
    if (this.selectedId() === sessionId) this.selectedId.set(null);
    await this.refresh();
  }

  cancel(): void {
    this.abort?.abort();
  }

  clearEvents(): void {
    const id = this.selectedId();
    if (id) this.update(id, (c) => ({ ...c, events: [] }));
  }

  async send(prompt: string): Promise<void> {
    const sessionId = this.selectedId();
    if (!sessionId || this.running()) return;

    this.update(sessionId, (c) => ({
      ...c,
      messages: [...c.messages, { id: crypto.randomUUID(), role: 'user', text: prompt }],
    }));
    this.running.set(true);
    this.abort = new AbortController();

    try {
      for await (const event of this.api.runAgui(sessionId, prompt, this.abort.signal)) {
        this.apply(sessionId, event);
      }
    } catch (e) {
      const text = this.abort.signal.aborted ? 'Run cancelled.' : describe(e);
      this.pushMessage(sessionId, { id: crypto.randomUUID(), role: 'error', text });
    } finally {
      this.running.set(false);
      await this.refresh();
    }
  }

  /** Logs the event and folds it into the chat transcript. */
  private apply(sessionId: string, event: AguiEvent): void {
    this.update(sessionId, (c) => ({
      ...c,
      events: [...c.events, { seq: ++this.seq, receivedAt: new Date(), event }],
    }));

    const e = event as Record<string, string | undefined>;
    switch (event.type) {
      case 'TEXT_MESSAGE_START':
        this.upsertMessage(sessionId, e['messageId']!, { role: 'assistant', text: '' }, (m) => m);
        break;
      case 'TEXT_MESSAGE_CONTENT':
      case 'TEXT_MESSAGE_CHUNK':
        this.upsertMessage(sessionId, e['messageId']!, { role: 'assistant', text: '' }, (m) => ({
          ...m,
          text: m.text + (e['delta'] ?? ''),
        }));
        break;
      case 'TOOL_CALL_START':
        this.upsertMessage(sessionId, e['toolCallId']!, { role: 'tool', text: '' }, (m) => ({
          ...m,
          toolName: e['toolCallName'],
        }));
        break;
      case 'TOOL_CALL_ARGS':
        this.upsertMessage(sessionId, e['toolCallId']!, { role: 'tool', text: '' }, (m) => ({
          ...m,
          toolArgs: (m.toolArgs ?? '') + (e['delta'] ?? ''),
        }));
        break;
      case 'TOOL_CALL_RESULT':
        this.upsertMessage(sessionId, e['toolCallId']!, { role: 'tool', text: '' }, (m) => ({
          ...m,
          toolResult: e['content'],
        }));
        break;
      case 'RUN_ERROR':
        this.pushMessage(sessionId, { id: crypto.randomUUID(), role: 'error', text: e['message'] ?? 'Run failed.' });
        break;
    }
  }

  private pushMessage(sessionId: string, message: ChatMessage): void {
    this.update(sessionId, (c) => ({ ...c, messages: [...c.messages, message] }));
  }

  private upsertMessage(
    sessionId: string,
    id: string,
    seed: Omit<ChatMessage, 'id'>,
    change: (m: ChatMessage) => ChatMessage,
  ): void {
    this.update(sessionId, (c) => {
      const exists = c.messages.some((m) => m.id === id);
      const messages = exists ? c.messages : [...c.messages, { id, ...seed }];
      return { ...c, messages: messages.map((m) => (m.id === id ? change(m) : m)) };
    });
  }

  private update(sessionId: string, change: (c: Conversation) => Conversation): void {
    this.conversations.update((all) => ({ ...all, [sessionId]: change(all[sessionId] ?? emptyConversation) }));
  }
}

function describe(e: unknown): string {
  if (e && typeof e === 'object' && 'message' in e) return String((e as { message: unknown }).message);
  return String(e);
}

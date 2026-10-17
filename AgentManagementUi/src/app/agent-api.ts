import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** All calls go through the dev-server proxy (proxy.conf.mjs) to AgentManagement. */
const baseUrl = '/api';

export interface SessionInfo {
  sessionId: string;
  runtimeVersion: string;
  startedAt: string;
  lastActivityAt: string;
  expiresAt: string;
}

export interface SessionResponse {
  sessionId: string;
  response: string | null;
}

/** A raw AG-UI event as received on the SSE stream. */
export interface AguiEvent {
  type: string;
  [key: string]: unknown;
}

@Injectable({ providedIn: 'root' })
export class AgentApi {
  private readonly http = inject(HttpClient);

  listSessions(): Promise<SessionInfo[]> {
    return firstValueFrom(this.http.get<SessionInfo[]>(`${baseUrl}/sessions`));
  }

  /** Allocates a session id; AgentCore starts the session's microVM on its first invocation. */
  createSession(): Promise<SessionResponse> {
    return firstValueFrom(this.http.post<SessionResponse>(`${baseUrl}/sessions`, {}));
  }

  stopSession(sessionId: string): Promise<unknown> {
    return firstValueFrom(this.http.delete(`${baseUrl}/sessions/${sessionId}`));
  }

  /**
   * Runs one AG-UI turn and yields the events as they arrive. The runtime keeps the conversation
   * history server side, so only the new user message is sent.
   */
  async *runAgui(sessionId: string, prompt: string, signal: AbortSignal): AsyncGenerator<AguiEvent> {
    const input = {
      threadId: sessionId,
      runId: crypto.randomUUID(),
      messages: [{ id: crypto.randomUUID(), role: 'user', content: prompt }],
      tools: [],
      context: [],
      state: {},
      forwardedProps: {},
    };

    const response = await fetch(`${baseUrl}/sessions/${sessionId}/agui`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
      body: JSON.stringify(input),
      signal,
    });
    if (!response.ok || !response.body) {
      throw new Error(`AG-UI request failed: ${response.status} ${response.statusText}`);
    }

    yield* readServerSentEvents(response.body);
  }
}

/** Parses an SSE stream whose `data:` fields carry JSON AG-UI events. */
async function* readServerSentEvents(body: ReadableStream<BufferSource>): AsyncGenerator<AguiEvent> {
  const reader = body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer = (buffer + value).replace(/\r\n?/g, '\n');

      let end: number;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        const data = block
          .split('\n')
          .filter((line) => line.startsWith('data:'))
          .map((line) => line.slice(5).replace(/^ /, ''))
          .join('\n');
        if (data) yield JSON.parse(data) as AguiEvent;
      }
    }
  } finally {
    reader.releaseLock();
  }
}

import { Injectable } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Observable, Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  ChatRequest, ConversationSummary, Conversation,
  IngestedDocument, SseEvent
} from '../models/models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly base = environment.apiUrl;
  private readonly headers = new HttpHeaders({
    'X-Api-Key': environment.apiKey,
    'Content-Type': 'application/json'
  });

  constructor(private http: HttpClient) {}

  // ── Streaming chat via SSE ──────────────────────
  streamChat(request: ChatRequest): Observable<SseEvent> {
    const subject = new Subject<SseEvent>();

    fetch(`${this.base}/api/chat/stream`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Api-Key': environment.apiKey
      },
      body: JSON.stringify(request)
    }).then(async (response) => {
      if (!response.ok) {
        subject.error(new Error(`HTTP ${response.status}`));
        return;
      }

      const reader = response.body!.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() ?? '';

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            try {
              const event: SseEvent = JSON.parse(line.slice(6));
              subject.next(event);
              if (event.type === 'done') {
                subject.complete();
                return;
              }
            } catch {
              // skip malformed lines
            }
          }
        }
      }
      subject.complete();
    }).catch(err => subject.error(err));

    return subject.asObservable();
  }

  // ── Conversations ───────────────────────────────
  getConversations(): Observable<ConversationSummary[]> {
    return this.http.get<ConversationSummary[]>(
      `${this.base}/api/chat/conversations`,
      { headers: this.headers }
    );
  }

  getConversation(id: string): Observable<Conversation> {
    return this.http.get<Conversation>(
      `${this.base}/api/chat/conversations/${id}`,
      { headers: this.headers }
    );
  }

  deleteConversation(id: string): Observable<void> {
    return this.http.delete<void>(
      `${this.base}/api/chat/conversations/${id}`,
      { headers: this.headers }
    );
  }

  // ── Documents ───────────────────────────────────
  getDocuments(): Observable<IngestedDocument[]> {
    return this.http.get<IngestedDocument[]>(
      `${this.base}/api/ingest/documents`,
      { headers: this.headers }
    );
  }

  ingestFile(file: File): Observable<any> {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post(
      `${this.base}/api/ingest`,
      formData,
      { headers: new HttpHeaders({ 'X-Api-Key': environment.apiKey }) }
    );
  }

  deleteDocument(fileName: string): Observable<any> {
    return this.http.delete(
      `${this.base}/api/ingest/documents/${encodeURIComponent(fileName)}`,
      { headers: this.headers }
    );
  }
}

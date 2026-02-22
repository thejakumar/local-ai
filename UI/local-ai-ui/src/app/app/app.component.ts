// import { Component, OnInit, ViewChild, ElementRef, AfterViewChecked } from '@angular/core';
import { Component, OnInit, ViewChild, ElementRef, 
         AfterViewChecked, ChangeDetectionStrategy, 
         ChangeDetectorRef, NgZone } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClientModule } from '@angular/common/http';
import { ApiService } from './services/api.service';
import { Message, ConversationSummary, RagSource, IngestedDocument } from './models/models';

@Component({
  selector: 'app-root',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, FormsModule, HttpClientModule],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit, AfterViewChecked {
  @ViewChild('messagesEl') messagesEl!: ElementRef;
  @ViewChild('inputEl') inputEl!: ElementRef;

  messages: Message[] = [];
  conversations: ConversationSummary[] = [];
  documents: IngestedDocument[] = [];

  inputText = '';
  currentConversationId?: string;
  selectedModel = 'llama3.2';
  useRag = true;
  isStreaming = false;
  sidebarCollapsed = false;
  uploadingFile = '';

  private streamController?: AbortController;
  private shouldScrollToBottom = false;

constructor(
  private api: ApiService,
  private cdr: ChangeDetectorRef,
  private ngZone: NgZone
) {}

  ngOnInit() {
    this.loadConversations();
    this.loadDocuments();
  }

  ngAfterViewChecked() {
    if (this.shouldScrollToBottom) {
      this.scrollToBottom();
      this.shouldScrollToBottom = false;
    }
  }

  // ── Send message ──────────────────────────────
sendMessage() {
  const text = this.inputText.trim();
  if (!text || this.isStreaming) return;

  this.inputText = '';
  this.resetTextarea();

  this.messages.push({ role: 'user', content: text });

  const aiMsg: Message = {
    role: 'assistant',
    content: '',
    streaming: true,
    sources: []
  };
  this.messages.push(aiMsg);
  this.shouldScrollToBottom = true;
  this.isStreaming = true;
  this.cdr.markForCheck();

  // Run OUTSIDE Angular zone for maximum speed
  this.ngZone.runOutsideAngular(() => {
    this.api.streamChat({
      conversationId: this.currentConversationId,
      message: text,
      useRag: this.useRag,
      model: this.selectedModel
    }).subscribe({
      next: (event) => {
        if (event.type === 'meta') {
          this.ngZone.run(() => {
            this.currentConversationId = event.conversationId;
            aiMsg.sources = event.sources;
            this.cdr.markForCheck();
          });
        } else if (event.type === 'token') {
          // Direct DOM manipulation for tokens = zero Angular overhead
          aiMsg.content += event.content;
          const msgEls = document.querySelectorAll('.ai-msg .msg-content');
          const last = msgEls[msgEls.length - 1] as HTMLElement;
          if (last) last.innerHTML = this.renderMarkdown(aiMsg.content);
          this.scrollToBottom();
        } else if (event.type === 'done') {
          this.ngZone.run(() => {
            aiMsg.streaming = false;
            this.isStreaming = false;
            this.cdr.markForCheck();
            this.loadConversations();
          });
        }
      },
      error: (err) => {
        this.ngZone.run(() => {
          console.error('Stream error:', err);
          aiMsg.content = '⚠️ Connection error. Is the API running?';
          aiMsg.streaming = false;
          this.isStreaming = false;
          this.cdr.markForCheck();
        });
      }
    });
  });
}

  stopStreaming() {
    this.streamController?.abort();
    this.isStreaming = false;
    const last = this.messages[this.messages.length - 1];
    if (last?.streaming) last.streaming = false;
  }

  sendSuggestion(text: string) {
    this.inputText = text;
    this.sendMessage();
  }

  onKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      this.sendMessage();
    }
  }

  autoResize(e: Event) {
    const el = e.target as HTMLTextAreaElement;
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 200) + 'px';
  }

  resetTextarea() {
    if (this.inputEl?.nativeElement) {
      this.inputEl.nativeElement.style.height = 'auto';
    }
  }

  // ── Conversations ─────────────────────────────
loadConversations() {
  this.api.getConversations().subscribe(c => {
    this.conversations = c;
    this.cdr.markForCheck();
  });
}


loadConversation(id: string) {
  this.currentConversationId = id;
  this.api.getConversation(id).subscribe(c => {
    // Force the array to be a new reference so Angular detects the change
    this.messages = c.messages.map(m => ({ ...m, streaming: false }));
    this.shouldScrollToBottom = true;
    this.cdr.markForCheck();
  });
}

newChat() {
  this.messages = [];
  this.currentConversationId = undefined;
  this.cdr.markForCheck();
}

deleteConversation(id: string, e: Event) {
  e.stopPropagation();
  this.api.deleteConversation(id).subscribe(() => {
    this.conversations = this.conversations.filter(c => c.id !== id);
    if (this.currentConversationId === id) {
      this.newChat();
    }
    this.cdr.markForCheck();
  });
}

  // ── Documents ─────────────────────────────────
loadDocuments() {
  this.api.getDocuments().subscribe(d => {
    this.documents = d;
    this.cdr.markForCheck();
  });
}

  onFileSelect(e: Event) {
    const files = (e.target as HTMLInputElement).files;
    if (files) this.ingestFiles(Array.from(files));
  }

  onDrop(e: DragEvent) {
    e.preventDefault();
    const files = Array.from(e.dataTransfer?.files ?? []);
    if (files.length) this.ingestFiles(files);
  }

  ingestFiles(files: File[]) {
    const ingestNext = (i: number) => {
      if (i >= files.length) {
        this.uploadingFile = '';
        this.loadDocuments();
        return;
      }
      this.uploadingFile = files[i].name;
      this.api.ingestFile(files[i]).subscribe({
        next: () => ingestNext(i + 1),
        error: (err) => {
          console.error('Ingest error:', err);
          ingestNext(i + 1);
        }
      });
    };
    ingestNext(0);
  }

  deleteDocument(fileName: string) {
    this.api.deleteDocument(fileName).subscribe(() => {
      this.documents = this.documents.filter(d => d.fileName !== fileName);
    });
  }

  // ── Helpers ───────────────────────────────────
  renderMarkdown(text: string): string {
    // Basic markdown rendering without dependency
    return text
      .replace(/```(\w+)?\n?([\s\S]*?)```/g, '<pre><code>$2</code></pre>')
      .replace(/`([^`]+)`/g, '<code>$1</code>')
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
      .replace(/\*([^*]+)\*/g, '<em>$1</em>')
      .replace(/^### (.+)$/gm, '<h3>$1</h3>')
      .replace(/^## (.+)$/gm, '<h2>$1</h2>')
      .replace(/^# (.+)$/gm, '<h1>$1</h1>')
      .replace(/^- (.+)$/gm, '<li>$1</li>')
      .replace(/\n\n/g, '</p><p>')
      .replace(/^(?!<[hlp]|<li|<pre)(.+)$/gm, '$1')
      || text;
  }

  getFileIcon(type: string): string {
    return { code: '⌨️', pdf: '📄', text: '📝' }[type] ?? '📁';
  }

  scrollToBottom() {
    try {
      const el = this.messagesEl.nativeElement;
      el.scrollTop = el.scrollHeight;
    } catch {}
  }
}

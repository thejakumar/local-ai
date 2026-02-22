export interface ChatRequest {
  conversationId?: string;
  message: string;
  useRag?: boolean;
  model?: string;
}

export interface RagSource {
  fileName: string;
  snippet: string;
  similarity: number;
}

export interface Message {
  id?: string;
  conversationId?: string;
  role: 'user' | 'assistant';
  content: string;
  model?: string;
  createdAt?: string;
  sources?: RagSource[];
  streaming?: boolean;
}

export interface Conversation {
  id: string;
  title: string;
  messages: Message[];
  updatedAt: string;
}

export interface ConversationSummary {
  id: string;
  title: string;
  updatedAt: string;
  messageCount: number;
}

export interface IngestedDocument {
  fileName: string;
  fileType: string;
  chunkCount: number;
  createdAt: string;
}

// SSE event shapes
export interface SseMetaEvent {
  type: 'meta';
  conversationId: string;
  sources: RagSource[];
}

export interface SseTokenEvent {
  type: 'token';
  content: string;
}

export interface SseDoneEvent {
  type: 'done';
  messageId: string;
}

export type SseEvent = SseMetaEvent | SseTokenEvent | SseDoneEvent;

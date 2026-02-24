export interface ChatRequest {
  conversationId?: string;
  message: string;
  useRag?: boolean;
  ragMode?: string;
  model?: string;
  fileTypeFilter?: string;
  fileNameFilter?: string;
}

export interface RagSource {
  fileName: string;
  snippet: string;
  similarity: number;
  searchType?: string;
}

export interface ConfidenceResult {
  overall: number;
  level: string;
  retrievalQuality: number;
  sourceCoverage: number;
}

export interface CitationCheck {
  fileName: string;
  verified: boolean;
  reason?: string;
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
  confidence?: ConfidenceResult;
  citations?: CitationCheck[];
  tokensUsed?: number;
}

export interface Conversation {
  id: string;
  title: string;
  messages: Message[];
  updatedAt: string;
  summary?: string;
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
  confidence?: ConfidenceResult;
  citations?: CitationCheck[];
  tokensUsed?: number;
}

export type SseEvent = SseMetaEvent | SseTokenEvent | SseDoneEvent;

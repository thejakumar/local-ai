# 🧠 Local AI — Self-Hosted RAG System

Full-stack local AI with Ollama + .NET 9 API + pgvector + Angular 18.

## Stack
| Layer | Tech |
|---|---|
| LLM Runtime | Ollama (llama3.2 / gemma3) |
| API | .NET 9 Web API |
| Vector DB | pgvector on Postgres 16 |
| Embeddings | nomic-embed-text via Ollama |
| Frontend | Angular 18 |
| Tunnel | Cloudflare (optional) |

## Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Node 20+](https://nodejs.org)
- [Docker Desktop](https://docker.com)
- [Ollama](https://ollama.ai)
- [Angular CLI](https://angular.io/cli): `npm install -g @angular/cli`

## Quick Start

### 1. Pull Ollama models
```bash
ollama pull llama3.2
ollama pull nomic-embed-text
```

### 2. Start Postgres + pgvector
```bash
docker-compose up -d
```

### 3. Run the API
```bash
cd backend/LocalAI.Api
dotnet run
# API runs on https://localhost:5001
```

### 4. Run the Angular UI
```bash
cd frontend
npm install
ng serve
# UI runs on http://localhost:4200
```

### 5. Ingest your files
```bash
# Drop files into /backend/LocalAI.Api/ingestion-watch/
# Or call the API directly:
curl -X POST https://localhost:5001/api/ingest \
  -F "file=@yourfile.pdf" \
  -H "X-Api-Key: your-secret-key"
```

## Project Structure
```
local-ai/
├── backend/
│   └── LocalAI.Api/
│       ├── Controllers/       # ChatController, IngestController
│       ├── Services/
│       │   ├── Ollama/        # OllamaService (chat + embeddings)
│       │   ├── Rag/           # RagService (retrieval + injection)
│       │   └── Embedding/     # EmbeddingService
│       ├── Models/            # Request/Response DTOs
│       ├── Data/              # EF Core DbContext + migrations
│       └── Middleware/        # ApiKeyMiddleware
├── frontend/
│   └── src/app/app/
│       ├── components/chat/   # Chat UI + streaming
│       ├── components/sidebar/# Conversation history
│       └── services/          # ApiService, ChatService
└── docker-compose.yml
```

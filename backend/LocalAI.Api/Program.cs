using LocalAI.Api.Data;
using LocalAI.Api.Middleware;
using LocalAI.Api.Services.Context;
using LocalAI.Api.Services.Embedding;
using LocalAI.Api.Services.Ollama;
using LocalAI.Api.Services.Prompts;
using LocalAI.Api.Services.Quality;
using LocalAI.Api.Services.Rag;
using LocalAI.Api.Services.Retrieval;
using LocalAI.Api.Services.Token;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsql => npgsql.UseVector()
    )
);

// ── HTTP Clients ──────────────────────────────────────
builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"]!);
    client.Timeout = TimeSpan.FromMinutes(5);
});

// ── Core Services ─────────────────────────────────────
builder.Services.AddScoped<IOllamaService, OllamaService>();
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<IChunkingService, ChunkingService>();
builder.Services.AddScoped<IRagService, RagService>();

// ── Advanced Retrieval Pipeline ───────────────────────
builder.Services.AddScoped<IHydeService, HydeService>();
builder.Services.AddScoped<IQueryExpansionService, QueryExpansionService>();
builder.Services.AddScoped<IRerankingService, RerankingService>();
builder.Services.AddScoped<IRetrievalPipeline, RetrievalPipeline>();

// ── Context Optimization ──────────────────────────────
builder.Services.AddScoped<IPromptTemplateService, PromptTemplateService>();
builder.Services.AddScoped<ITokenBudgetService, TokenBudgetService>();
builder.Services.AddScoped<IConversationSummaryService, ConversationSummaryService>();
builder.Services.AddScoped<IContextCompressionService, ContextCompressionService>();

// ── Quality Assurance ─────────────────────────────────
builder.Services.AddScoped<IConfidenceScorer, ConfidenceScorer>();
builder.Services.AddScoped<ICitationVerifier, CitationVerifier>();

// ── CORS (Angular dev server) ─────────────────────────
builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
    )
);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── Auto-migrate on startup ───────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
app.MapSwagger();

app.UseCors();
//app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();

app.Run();

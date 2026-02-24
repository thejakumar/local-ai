using LocalAI.Api.Data;
using LocalAI.Api.Middleware;
using LocalAI.Api.Services.Embedding;
using LocalAI.Api.Services.Ollama;
using LocalAI.Api.Services.Rag;
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

// ── Services ──────────────────────────────────────────
builder.Services.AddScoped<IOllamaService, OllamaService>();
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<IChunkingService, ChunkingService>();
builder.Services.AddScoped<IRagService, RagService>();

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
    await db.Database.EnsureCreatedAsync();
}

app.UseSwagger();       // ← Add these 3 lines
app.UseSwaggerUI();
app.MapSwagger();

app.UseCors();
//app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();

app.Run();

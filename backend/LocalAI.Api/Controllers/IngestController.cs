using LocalAI.Api.Data;
using LocalAI.Api.Services.Rag;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAI.Api.Controllers;

[ApiController]
[Route("api/ingest")]
public class IngestController(
    IRagService rag,
    AppDbContext db,
    ILogger<IngestController> logger) : ControllerBase
{
    // ── POST /api/ingest  (file upload) ─────────────
    [HttpPost]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50MB limit
    public async Task<IActionResult> IngestFile(
        IFormFile file,
        CancellationToken ct)
    {
        if (file.Length == 0)
            return BadRequest(new { error = "File is empty" });

        logger.LogInformation("Ingesting file: {Name} ({Size} bytes)", file.FileName, file.Length);

        await using var stream = file.OpenReadStream();
        var result = await rag.IngestFileAsync(file.FileName, stream, ct);

        return Ok(result);
    }

    // ── POST /api/ingest/text  (raw text/code) ───────
    [HttpPost("text")]
    public async Task<IActionResult> IngestText(
        [FromBody] IngestTextRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Content is empty" });

        var result = await rag.IngestTextAsync(
            request.FileName,
            request.Content,
            request.FileType ?? "text",
            ct);

        return Ok(result);
    }

    // ── GET /api/ingest/documents ────────────────────
    [HttpGet("documents")]
    public async Task<IActionResult> ListDocuments(CancellationToken ct)
    {
        var docs = await db.Documents
            .GroupBy(d => new { d.FileName, d.FileType })
            .Select(g => new
            {
                g.Key.FileName,
                g.Key.FileType,
                ChunkCount = g.Count(),
                CreatedAt = g.Min(d => d.CreatedAt)
            })
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        return Ok(docs);
    }

    // ── DELETE /api/ingest/documents/{fileName} ──────
    [HttpDelete("documents/{fileName}")]
    public async Task<IActionResult> DeleteDocument(string fileName, CancellationToken ct)
    {
        var docs = await db.Documents
            .Where(d => d.FileName == fileName)
            .ToListAsync(ct);

        if (docs.Count == 0) return NotFound();

        db.Documents.RemoveRange(docs);
        await db.SaveChangesAsync(ct);

        return Ok(new { deleted = docs.Count, fileName });
    }

    // ── GET /api/health ──────────────────────────────
    [HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "ok", timestamp = DateTime.UtcNow });
}

public record IngestTextRequest(string FileName, string Content, string? FileType);

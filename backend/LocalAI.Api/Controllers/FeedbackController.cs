using LocalAI.Api.Data;
using LocalAI.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAI.Api.Controllers;

[ApiController]
[Route("api/feedback")]
public class FeedbackController(AppDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> SubmitFeedback([FromBody] FeedbackRequest request, CancellationToken ct)
    {
        var message = await db.Messages.FindAsync([request.MessageId], ct);
        if (message is null) return NotFound();

        var feedback = new MessageFeedback
        {
            MessageId = request.MessageId,
            Helpful = request.Helpful
        };

        db.Set<MessageFeedback>().Add(feedback);
        await db.SaveChangesAsync(ct);

        return Ok(new { feedback.Id, feedback.Helpful });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var stats = await db.Set<MessageFeedback>()
            .GroupBy(f => f.Helpful)
            .Select(g => new { Helpful = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return Ok(new
        {
            Total = stats.Sum(s => s.Count),
            Helpful = stats.FirstOrDefault(s => s.Helpful)?.Count ?? 0,
            Unhelpful = stats.FirstOrDefault(s => !s.Helpful)?.Count ?? 0
        });
    }
}

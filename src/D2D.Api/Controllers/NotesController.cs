using System.ComponentModel.DataAnnotations;
using D2D.Api.Auth;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Persistence.AppData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace D2D.Api.Controllers;

public sealed record CreateNoteRequest([Required, MaxLength(200)] string Title, string? Body);

/// <summary>
/// Sample app-data module: reads and writes the calling app's own database,
/// whichever engine that app is configured with.
/// </summary>
[ApiController]
[Route("api/v1/notes")]
[Authorize(Policy = Policies.AppUser)]
public sealed class NotesController(ITenantDbContextFactory dbFactory) : ControllerBase
{
    [HttpGet]
    public async Task<List<Note>> List(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync<AppDataDbContext>(ct);
        var userId = User.GetUserId();
        return await db.Notes.AsNoTracking()
            .Where(n => n.OwnerUserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    [HttpPost]
    public async Task<Note> Create(CreateNoteRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync<AppDataDbContext>(ct);
        var note = new Note { OwnerUserId = User.GetUserId(), Title = request.Title, Body = request.Body };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return note;
    }
}

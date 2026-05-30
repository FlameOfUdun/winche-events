using System.Text.Json;
using System.Text.Json.Serialization;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Winche.Events.Abstractions;
using Winche.Events.Commands;
using Winche.Events.Commands.DependencyInjection;
using Winche.Events.DependencyInjection;
using Winche.Events.Projection;
using Winche.Events.Session;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "your-connection-string-here";

builder.Services.AddWincheEvents(opts =>
{
    opts.ConnectionString = connectionString;
    opts.AddEvent<NoteCreated>("NoteCreated");
    opts.AddEvent<NoteUpdated>("NoteUpdated");
    opts.AddEvent<NoteDeleted>("NoteDeleted");
    opts.AddProjection<NoteProjection, Note>(ProjectionMode.Inline);
    opts.ConfigureJsonSerializer = o =>
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    };
});

builder.Services.AddWincheEventsCommands(cmds =>
    cmds.AddCommandHandler<NoteCommandHandler, Note>());

var app = builder.Build();
app.UseCors();

// Initialise Marten schema on startup
await using (var scope = app.Services.CreateAsyncScope())
{
    var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
    await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
}

// Command registry: maps commandType string → Command<Note> factory
var commandRegistry = new Dictionary<string, Func<JsonElement, DateTimeOffset, Command<Note>>>
{
    ["CreateNoteCommand"] = (p, ts) => new CreateNoteCommand(
        p.GetProperty("title").GetString()!,
        p.TryGetProperty("content", out var cc) ? cc.GetString() ?? "" : "") { CreatedAt = ts },

    ["UpdateNoteCommand"] = (p, ts) => new UpdateNoteCommand(
        p.GetProperty("title").GetString()!,
        p.TryGetProperty("content", out var uc) ? uc.GetString() ?? "" : "") { CreatedAt = ts },

    ["DeleteNoteCommand"] = (_, ts) => new DeleteNoteCommand() { CreatedAt = ts },
};

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

// POST /api/dispatch  — streamId lives in the body so slashes are never a routing problem
app.MapPost("/api/dispatch", async (
    [FromBody] DispatchRequest request,
    ICommandDispatcher dispatcher) =>
{
    if (!commandRegistry.TryGetValue(request.CommandType, out var factory))
        return Results.BadRequest(new { error = $"Unknown command type: {request.CommandType}" });

    var command = factory(request.Payload, request.CreatedAt);

    try
    {
        var result = await dispatcher.DispatchAsync(request.StreamId, command);
        var events = result.Events.Select(e => new EventDto(
            e.Id,
            e.Data.GetType().Name,
            JsonSerializer.SerializeToElement(e.Data, e.Data.GetType(), jsonOpts),
            e.Version,
            e.Timestamp,
            e.Sequence));

        return Results.Ok(new DispatchResponse(result.Version, events));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// GET /api/notes/{noteId}
app.MapGet("/api/notes/{noteId}", async (string noteId, IEventStore eventStore) =>
{
    await using var session = await eventStore.OpenSessionAsync();
    var note = await session.GetStateAsync<Note>(noteId);
    if (note is null || note.IsDeleted)
        return Results.NotFound();
    return Results.Ok(new NoteDto(note.Id, note.Title, note.Content, note.IsDeleted));
});

// GET /api/notes
app.MapGet("/api/notes", async (IEventStore eventStore) =>
{
    await using var session = await eventStore.OpenSessionAsync();
    var notes = await session.QueryStatesAsync<Note>(q => q.Where(n => !n.IsDeleted));
    return Results.Ok(notes.Select(n => new NoteDto(n.Id, n.Title, n.Content, n.IsDeleted)));
});

app.Run();

// ── Domain ────────────────────────────────────────────────────────────────────

record Note(string Title, string Content, bool IsDeleted) : Aggregate
{
    public static Note Empty => new("", "", false);
}

record NoteCreated(string Title, string Content) : Event;
record NoteUpdated(string Title, string Content) : Event;
record NoteDeleted() : Event;

class NoteProjection : Projection<Note>
{
    public NoteProjection()
    {
        On<NoteCreated>((s, e) => s with { Title = e.Data.Title, Content = e.Data.Content });
        On<NoteUpdated>((s, e) => s with { Title = e.Data.Title, Content = e.Data.Content });
        On<NoteDeleted>((s, e) => s with { IsDeleted = true });
    }
    public override Note Create(string id) => Note.Empty with { Id = id };
}

record CreateNoteCommand(string Title, string Content) : Command<Note>;
record UpdateNoteCommand(string Title, string Content) : Command<Note>;
record DeleteNoteCommand() : Command<Note>;

class NoteCommandHandler : CommandHandler<Note>
{
    public NoteCommandHandler()
    {
        On<CreateNoteCommand>((state, cmd) =>
        {
            if (state is not null && !state.IsDeleted)
                throw new InvalidOperationException("Note already exists.");
            return [new NoteCreated(cmd.Title, cmd.Content)];
        });

        On<UpdateNoteCommand>((state, cmd) =>
        {
            if (state is null or { IsDeleted: true })
                throw new InvalidOperationException("Note not found.");
            return [new NoteUpdated(cmd.Title, cmd.Content)];
        });

        On<DeleteNoteCommand>((state, cmd) =>
        {
            if (state is null or { IsDeleted: true })
                throw new InvalidOperationException("Note not found.");
            return [new NoteDeleted()];
        });
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

record DispatchRequest(string StreamId, string CommandType, JsonElement Payload, DateTimeOffset CreatedAt);
record EventDto(string Id, string EventType, JsonElement Data, long Version, DateTimeOffset Timestamp, long Sequence);
record DispatchResponse(long Version, IEnumerable<EventDto> Events);
record NoteDto(string Id, string Title, string Content, bool IsDeleted);

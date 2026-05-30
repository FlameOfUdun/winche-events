using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;

namespace Winche.Events.WebApi.Tests;

// DTOs matching the API response shapes
record EventResultDto(
    string Id,
    string EventType,
    JsonElement Data,
    long Version,
    DateTimeOffset Timestamp,
    long Sequence);

record DispatchResultDto(long Version, IEnumerable<EventResultDto> Events);

record NoteDto(string Id, string Title, string Content, bool IsDeleted);

public class NotesApiTests
{
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("http://localhost:5000") };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string NewNoteId() => $"notes/{Guid.NewGuid():N}";

    private Task<HttpResponseMessage> Dispatch(string noteId, string commandType, object payload) =>
        Client.PostAsJsonAsync($"/api/notes/{noteId}/commands", new
        {
            commandType,
            payload,
            createdAt = DateTimeOffset.UtcNow
        }, JsonOpts);

    // ── Create ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_returns_200_with_NoteCreated_event()
    {
        var noteId = NewNoteId();
        var response = await Dispatch(noteId, "CreateNoteCommand", new { title = "Buy milk" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<DispatchResultDto>(JsonOpts);
        result!.Version.Should().Be(1);
        result.Events.Should().ContainSingle(e => e.EventType == "NoteCreated");
    }

    [Fact]
    public async Task Create_note_is_readable_via_GET()
    {
        var noteId = NewNoteId();
        await Dispatch(noteId, "CreateNoteCommand", new { title = "Read me" });

        var response = await Client.GetAsync($"/api/notes/{noteId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var note = await response.Content.ReadFromJsonAsync<NoteDto>(JsonOpts);
        note!.Title.Should().Be("Read me");
        note.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Create_returns_400_if_note_already_exists()
    {
        var noteId = NewNoteId();
        await Dispatch(noteId, "CreateNoteCommand", new { title = "First" });

        var response = await Dispatch(noteId, "CreateNoteCommand", new { title = "Duplicate" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("already exists");
    }

    // ── Update ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_changes_title_and_returns_NoteUpdated_event()
    {
        var noteId = NewNoteId();
        await Dispatch(noteId, "CreateNoteCommand", new { title = "Original" });

        var response = await Dispatch(noteId, "UpdateNoteCommand", new { title = "Updated title" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<DispatchResultDto>(JsonOpts);
        result!.Version.Should().Be(2);
        result.Events.Should().ContainSingle(e => e.EventType == "NoteUpdated");

        var getResponse = await Client.GetAsync($"/api/notes/{noteId}");
        var note = await getResponse.Content.ReadFromJsonAsync<NoteDto>(JsonOpts);
        note!.Title.Should().Be("Updated title");
    }

    [Fact]
    public async Task Update_returns_400_for_nonexistent_note()
    {
        var noteId = NewNoteId();
        var response = await Dispatch(noteId, "UpdateNoteCommand", new { title = "Ghost" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not found");
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_marks_note_as_deleted_and_returns_NoteDeleted_event()
    {
        var noteId = NewNoteId();
        await Dispatch(noteId, "CreateNoteCommand", new { title = "Delete me" });

        var response = await Dispatch(noteId, "DeleteNoteCommand", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<DispatchResultDto>(JsonOpts);
        result!.Events.Should().ContainSingle(e => e.EventType == "NoteDeleted");

        var getResponse = await Client.GetAsync($"/api/notes/{noteId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_returns_400_for_nonexistent_note()
    {
        var noteId = NewNoteId();
        var response = await Dispatch(noteId, "DeleteNoteCommand", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not found");
    }

    // ── Event envelope ───────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchResult_events_have_valid_metadata()
    {
        var noteId = NewNoteId();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var response = await Dispatch(noteId, "CreateNoteCommand", new { title = "Metadata check" });
        var result = await response.Content.ReadFromJsonAsync<DispatchResultDto>(JsonOpts);

        var e = result!.Events.First();
        e.Id.Should().NotBeNullOrEmpty();
        e.Version.Should().Be(1);
        e.Sequence.Should().BeGreaterThan(0);
        e.Timestamp.Should().BeAfter(before);
        e.Data.TryGetProperty("title", out var titleProp).Should().BeTrue();
        titleProp.GetString().Should().Be("Metadata check");
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_includes_created_note()
    {
        var noteId = NewNoteId();
        await Dispatch(noteId, "CreateNoteCommand", new { title = "In the list" });

        var response = await Client.GetAsync("/api/notes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var notes = await response.Content.ReadFromJsonAsync<List<NoteDto>>(JsonOpts);
        notes.Should().Contain(n => n.Id == noteId && n.Title == "In the list");
    }

    [Fact]
    public async Task GetAll_excludes_deleted_notes()
    {
        var noteId = NewNoteId();
        await Dispatch(noteId, "CreateNoteCommand", new { title = "Will be deleted" });
        await Dispatch(noteId, "DeleteNoteCommand", new { });

        var response = await Client.GetAsync("/api/notes");
        var notes = await response.Content.ReadFromJsonAsync<List<NoteDto>>(JsonOpts);
        notes.Should().NotContain(n => n.Id == noteId);
    }

    // ── Unknown command ───────────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_commandType_returns_400()
    {
        var response = await Dispatch(NewNoteId(), "BogusCommand", new { });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Unknown command type");
    }

    // ── GET nonexistent note ───────────────────────────────────────────────────

    [Fact]
    public async Task Get_nonexistent_note_returns_404()
    {
        var response = await Client.GetAsync($"/api/notes/{NewNoteId()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

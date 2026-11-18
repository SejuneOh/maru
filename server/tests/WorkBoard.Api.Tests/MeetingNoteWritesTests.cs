using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class MeetingNoteWritesTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string login, HttpMethod method, string url, object? body = null, string? key = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Dev-User", login);
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
    }

    private static Task<Guid> MemberIdAsync(TestTenant t, string login) =>
        t.ScalarAsync<Guid>("select id from members where tenant_id = @tid and github_login = @login", new { tid = t.Id, login });

    private static Task<int> CountAsync(TestTenant t, string sql) => t.ScalarAsync<int>(sql, new { tid = t.Id });

    private static string[] ItemTexts(JsonElement note) =>
        note.GetProperty("actionItems").EnumerateArray().Select(a => a.GetProperty("text").GetString()!).ToArray();

    private static object Body(
        string title, Guid[] attendees, object[] items, string[]? decisions = null, string? expected = null, Guid? projectId = null) => new
    {
        title, heldAt = "2026-11-12T10:00:00+09:00", projectId, bodyMd = "## 안건\n- 하나", decisions = decisions ?? ["배포는 금요일"],
        attendeeIds = attendees, actionItems = items, expectedUpdatedAt = expected,
    };

    [DbFact]
    public async Task Create_saves_attendees_decisions_and_items_in_one_request()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (other, _) = await t.AddMemberAsync("other");
        var me = await MemberIdAsync(t, t.ViewerLogin);
        var otherId = await MemberIdAsync(t, other);
        var project = await t.AddProjectAsync("p");

        var (status, note) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes",
            Body(" 주간 회의 ", [me, otherId, me],
                [new { text = "알림 설계", assigneeId = otherId, dueDate = "2026-11-20" }, new { text = "  점검  " }],
                decisions: ["  배포는 금요일 ", "", "범위 고정"], projectId: project));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("주간 회의", note.GetProperty("title").GetString());
        Assert.Equal(["배포는 금요일", "범위 고정"], note.GetProperty("decisions").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(2, note.GetProperty("attendees").GetArrayLength());
        Assert.Equal(["알림 설계", "점검"], ItemTexts(note));
        var first = note.GetProperty("actionItems")[0];
        Assert.Equal("2026-11-20", first.GetProperty("dueDate").GetString());
        Assert.Equal(otherId, first.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, note.GetProperty("actionItems")[1].GetProperty("dueDate").ValueKind);
        Assert.Equal(me, note.GetProperty("createdBy").GetProperty("id").GetGuid());
        Assert.Equal(project, note.GetProperty("project").GetProperty("id").GetGuid());
        Assert.Equal(1, await CountAsync(t, "select count(*)::int from audit_log where tenant_id = @tid and action = 'meeting_note.create'"));

        var id = note.GetProperty("id").GetGuid();
        var read = await t.GetAsync($"/api/meeting-notes/{id}");
        Assert.Equal(note.GetProperty("updatedAt").GetString(), read.GetProperty("updatedAt").GetString());
        Assert.Equal(["알림 설계", "점검"], ItemTexts(read));
    }

    [DbFact]
    public async Task Create_refuses_people_and_projects_outside_the_tenant_and_writes_nothing()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var outside = await TestTenant.CreateAsync(factory);
        var stranger = await MemberIdAsync(outside, outside.ViewerLogin);
        var foreignProject = await outside.AddProjectAsync("x");
        var me = await MemberIdAsync(t, t.ViewerLogin);

        var attendee = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("a", [stranger], []));
        var owner = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes",
            Body("a", [me], [new { text = "x", assigneeId = stranger }]));
        var project = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("a", [me], [], projectId: foreignProject));
        var noTitle = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("  ", [me], []));
        var emptyItem = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("a", [me], [new { text = " " }]));

        foreach (var r in new[] { attendee, owner, project, noTitle, emptyItem })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.Equal("invalid_request", r.Body.GetProperty("error").GetString());
        }
        Assert.Equal(0, await CountAsync(t, "select count(*)::int from meeting_notes where tenant_id = @tid"));
    }

    [DbFact]
    public async Task Departed_members_cannot_be_newly_added_but_stay_on_notes_that_already_have_them()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (gone, _) = await t.AddMemberAsync("gone");
        var me = await MemberIdAsync(t, t.ViewerLogin);
        var goneId = await MemberIdAsync(t, gone);

        var (_, created) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes",
            Body("a", [me, goneId], [new { text = "x", assigneeId = goneId }]));
        var id = created.GetProperty("id").GetGuid();
        var itemId = created.GetProperty("actionItems")[0].GetProperty("id").GetGuid();
        var updatedAt = created.GetProperty("updatedAt").GetString();
        var (_, plain) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("b", [me], [new { text = "y" }]));
        await t.ExecAsync("update members set left_at = now() where id = @goneId", new { goneId });

        var asAttendee = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("c", [me, goneId], []));
        var asAssignee = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes",
            Body("c", [me], [new { text = "z", assigneeId = goneId }]));
        var plainId = plain.GetProperty("id").GetGuid();
        var plainAt = plain.GetProperty("updatedAt").GetString();
        var addAttendee = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{plainId}",
            Body("b", [me, goneId], [new { text = "y" }], expected: plainAt));
        var addAssignee = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{plainId}",
            Body("b", [me], [new { text = "y", assigneeId = goneId }], expected: plainAt));
        var newItemForGone = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            Body("a2", [me, goneId], [new { id = itemId, text = "x", assigneeId = goneId }, new { text = "new", assigneeId = goneId }],
                expected: updatedAt));

        foreach (var r in new[] { asAttendee, asAssignee, addAttendee, addAssignee, newItemForGone })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.Equal("invalid_request", r.Body.GetProperty("error").GetString());
        }

        var edited = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            Body("a2", [me, goneId], [new { id = itemId, text = "x2", assigneeId = goneId }], expected: updatedAt));
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        Assert.Equal(2, edited.Body.GetProperty("attendees").GetArrayLength());
        Assert.Equal(goneId, edited.Body.GetProperty("actionItems")[0].GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(t.ViewerLogin, HttpMethod.Get, $"/api/meeting-notes/{id}")).Status);
    }

    [DbFact]
    public async Task Create_with_the_same_key_makes_one_note()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var me = await MemberIdAsync(t, t.ViewerLogin);
        var body = Body("a", [me], [new { text = "x" }]);

        var first = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", body, key: "k1");
        var again = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", body, key: "k1");

        Assert.Equal(HttpStatusCode.Created, again.Status);
        Assert.Equal(first.Body.GetProperty("id").GetString(), again.Body.GetProperty("id").GetString());
        Assert.Equal(1, await CountAsync(t, "select count(*)::int from meeting_notes where tenant_id = @tid"));
    }

    [DbFact]
    public async Task Patch_keeps_item_ids_adds_and_removes_and_checks_updated_at()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (other, _) = await t.AddMemberAsync("other");
        var me = await MemberIdAsync(t, t.ViewerLogin);
        var otherId = await MemberIdAsync(t, other);
        var (_, created) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes",
            Body("a", [me, otherId], [new { text = "keep" }, new { text = "drop" }]));
        var id = created.GetProperty("id").GetGuid();
        var keepId = created.GetProperty("actionItems")[0].GetProperty("id").GetGuid();
        var updatedAt = created.GetProperty("updatedAt").GetString();

        var (status, patched) = await SendAsync(other, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            Body("b", [otherId], [new { text = "new" }, new { id = keepId, text = "kept", assigneeId = me, dueDate = "2026-12-01" }],
                decisions: [], expected: updatedAt));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("b", patched.GetProperty("title").GetString());
        Assert.Equal(["new", "kept"], ItemTexts(patched));
        Assert.Equal(keepId, patched.GetProperty("actionItems")[1].GetProperty("id").GetGuid());
        Assert.Equal("2026-12-01", patched.GetProperty("actionItems")[1].GetProperty("dueDate").GetString());
        Assert.Equal([otherId], patched.GetProperty("attendees").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()));
        Assert.Equal(0, patched.GetProperty("decisions").GetArrayLength());
        Assert.NotEqual(updatedAt, patched.GetProperty("updatedAt").GetString());

        var stale = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            Body("c", [me], [], expected: updatedAt));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("conflict", stale.Body.GetProperty("error").GetString());

        var fresh = patched.GetProperty("updatedAt").GetString();
        var foreignItem = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            Body("c", [me], [new { id = Guid.NewGuid(), text = "x" }], expected: fresh));
        Assert.Equal(HttpStatusCode.BadRequest, foreignItem.Status);
        var missingLists = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            new { title = "c", heldAt = "2026-11-12T10:00:00+09:00", expectedUpdatedAt = fresh });
        Assert.Equal(HttpStatusCode.BadRequest, missingLists.Status);
        var noExpected = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{id}", Body("c", [me], []));
        Assert.Equal(HttpStatusCode.BadRequest, noExpected.Status);
        var missing = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{Guid.NewGuid()}", Body("c", [me], [], expected: fresh));
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);

        Assert.Equal(1, await CountAsync(t, "select count(*)::int from audit_log where tenant_id = @tid and action = 'meeting_note.update'"));
    }

    [DbFact]
    public async Task Patch_refuses_to_drop_an_item_linked_to_an_issue()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await t.AddRepoAsync("r");
        var issue = await t.AddIssueAsync(repo);
        var me = await MemberIdAsync(t, t.ViewerLogin);
        var (_, created) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("a", [me], [new { text = "linked" }]));
        var id = created.GetProperty("id").GetGuid();
        var itemId = created.GetProperty("actionItems")[0].GetProperty("id").GetGuid();
        await t.ExecAsync("update meeting_action_items set issue_id = @issue where id = @itemId", new { issue, itemId });

        var (status, body) = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            Body("b", [me], [new { text = "other" }], expected: created.GetProperty("updatedAt").GetString()));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("action_item_linked", body.GetProperty("error").GetString());
        Assert.Equal([itemId], body.GetProperty("actionItemIds").EnumerateArray().Select(x => x.GetGuid()));
        var read = await t.GetAsync($"/api/meeting-notes/{id}");
        Assert.Equal("a", read.GetProperty("title").GetString());
        Assert.Equal(["linked"], ItemTexts(read));

        var (editStatus, edited) = await SendAsync(t.ViewerLogin, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            Body("b", [me], [new { id = itemId, text = "renamed" }], expected: created.GetProperty("updatedAt").GetString()));
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(issue, edited.GetProperty("actionItems")[0].GetProperty("issue").GetProperty("id").GetGuid());
    }

    [DbFact]
    public async Task Only_the_author_or_an_admin_deletes()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (other, _) = await t.AddMemberAsync("other");
        var (admin, _) = await t.AddMemberAsync("admin");
        await t.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_login = @admin", new { tid = t.Id, admin });
        var me = await MemberIdAsync(t, t.ViewerLogin);
        var (_, a) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("a", [me], [new { text = "x" }]));
        var (_, b) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("b", [me], []));

        var refused = await SendAsync(other, HttpMethod.Delete, $"/api/meeting-notes/{a.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("forbidden", refused.Body.GetProperty("error").GetString());

        var (byAuthor, _) = await SendAsync(t.ViewerLogin, HttpMethod.Delete, $"/api/meeting-notes/{a.GetProperty("id").GetGuid()}");
        var (byAdmin, _) = await SendAsync(admin, HttpMethod.Delete, $"/api/meeting-notes/{b.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, byAuthor);
        Assert.Equal(HttpStatusCode.NoContent, byAdmin);
        var (again, _) = await SendAsync(admin, HttpMethod.Delete, $"/api/meeting-notes/{b.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, again);

        Assert.Equal(0, await CountAsync(t, "select count(*)::int from meeting_notes where tenant_id = @tid"));
        Assert.Equal(0, await CountAsync(t, "select count(*)::int from meeting_action_items where tenant_id = @tid"));
        Assert.Equal(2, await CountAsync(t, "select count(*)::int from audit_log where tenant_id = @tid and action = 'meeting_note.delete'"));
    }

    private static object NewBody(Guid id, string title, Guid[] attendees) => new
    {
        id, title, heldAt = "2026-11-12T10:00:00+09:00", attendeeIds = attendees, actionItems = new[] { new { text = "x" } },
    };

    [DbFact]
    public async Task Post_with_a_client_id_creates_the_note_once()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var outside = await TestTenant.CreateAsync(factory);
        var (other, _) = await t.AddMemberAsync("other");
        var me = await MemberIdAsync(t, t.ViewerLogin);
        var id = Guid.NewGuid();

        var (created, first) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", NewBody(id, "first", [me]), key: "a");
        var (repeated, again) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", NewBody(id, "typed more", [me]), key: "b");
        var (byOther, otherBody) = await SendAsync(other, HttpMethod.Post, "/api/meeting-notes", NewBody(id, "theirs", []));
        var (foreign, foreignBody) = await SendAsync(outside.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", NewBody(id, "elsewhere", []));

        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal(id, first.GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("createdAt").GetString(), first.GetProperty("updatedAt").GetString());
        Assert.Equal(HttpStatusCode.OK, repeated);
        Assert.Equal("first", again.GetProperty("title").GetString());
        Assert.Equal(first.GetProperty("updatedAt").GetString(), again.GetProperty("updatedAt").GetString());
        Assert.Equal(HttpStatusCode.Conflict, byOther);
        Assert.Equal("id_taken", otherBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, foreign);
        Assert.Equal("id_taken", foreignBody.GetProperty("error").GetString());
        Assert.Equal(1, await CountAsync(t, "select count(*)::int from meeting_notes where tenant_id = @tid"));
        Assert.Equal(1, await CountAsync(t, "select count(*)::int from meeting_action_items where tenant_id = @tid"));
        Assert.Equal(0, await CountAsync(outside, "select count(*)::int from meeting_notes where tenant_id = @tid"));
        Assert.Equal(1, await CountAsync(t, "select count(*)::int from audit_log where tenant_id = @tid and action = 'meeting_note.create'"));

        // The editor treats a replayed create whose updatedAt moved past createdAt as someone else's edit.
        var (_, edited) = await SendAsync(other, HttpMethod.Patch, $"/api/meeting-notes/{id}",
            Body("edited", [me], [], expected: first.GetProperty("updatedAt").GetString()));
        var (replayed, afterEdit) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", NewBody(id, "mine again", [me]), key: "c");
        Assert.Equal(HttpStatusCode.OK, replayed);
        Assert.Equal("edited", afterEdit.GetProperty("title").GetString());
        Assert.Equal(edited.GetProperty("updatedAt").GetString(), afterEdit.GetProperty("updatedAt").GetString());
        Assert.NotEqual(afterEdit.GetProperty("createdAt").GetString(), afterEdit.GetProperty("updatedAt").GetString());
    }

    [DbFact]
    public async Task Post_refuses_an_empty_id_and_items_with_ids()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var me = await MemberIdAsync(t, t.ViewerLogin);

        var emptyId = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", NewBody(Guid.Empty, "a", [me]));
        var itemId = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes",
            Body("a", [me], [new { id = Guid.NewGuid(), text = "x" }]));

        foreach (var r in new[] { emptyId, itemId })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.Equal("invalid_request", r.Body.GetProperty("error").GetString());
        }
        Assert.Equal(0, await CountAsync(t, "select count(*)::int from meeting_notes where tenant_id = @tid"));
    }

    [DbFact]
    public async Task Patch_refuses_item_ids_that_are_not_this_notes()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var outside = await TestTenant.CreateAsync(factory);
        var me = await MemberIdAsync(t, t.ViewerLogin);
        var (_, note) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("a", [me], [new { text = "mine" }]));
        var (_, otherNote) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("b", [me], [new { text = "theirs" }]));
        var outsider = await MemberIdAsync(outside, outside.ViewerLogin);
        var (_, foreignNote) = await SendAsync(outside.ViewerLogin, HttpMethod.Post, "/api/meeting-notes", Body("c", [outsider], [new { text = "far" }]));
        var id = note.GetProperty("id").GetGuid();
        var mine = note.GetProperty("actionItems")[0].GetProperty("id").GetGuid();
        var expected = note.GetProperty("updatedAt").GetString();
        var url = $"/api/meeting-notes/{id}";

        var fromOtherNote = await SendAsync(t.ViewerLogin, HttpMethod.Patch, url,
            Body("a", [me], [new { id = otherNote.GetProperty("actionItems")[0].GetProperty("id").GetGuid(), text = "x" }], expected: expected));
        var duplicate = await SendAsync(t.ViewerLogin, HttpMethod.Patch, url,
            Body("a", [me], [new { id = mine, text = "x" }, new { id = mine, text = "y" }], expected: expected));
        var fromOtherTenant = await SendAsync(t.ViewerLogin, HttpMethod.Patch, url,
            Body("a", [me], [new { id = foreignNote.GetProperty("actionItems")[0].GetProperty("id").GetGuid(), text = "x" }], expected: expected));
        var noExpected = await SendAsync(t.ViewerLogin, HttpMethod.Patch, url, Body("a", [me], [new { id = mine, text = "x" }]));

        foreach (var r in new[] { fromOtherNote, duplicate, fromOtherTenant, noExpected })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.Equal("invalid_request", r.Body.GetProperty("error").GetString());
        }
        var read = await t.GetAsync(url);
        Assert.Equal(expected, read.GetProperty("updatedAt").GetString());
        Assert.Equal(["mine"], ItemTexts(read));
        Assert.Equal(["far"], ItemTexts(await outside.GetAsync($"/api/meeting-notes/{foreignNote.GetProperty("id").GetGuid()}")));
    }
}

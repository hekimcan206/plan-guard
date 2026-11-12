using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PlanGuard.Reporting;

namespace PlanGuard.GitHub;

/// <summary>
/// Creates or updates the plan-guard comment on a pull request through the REST API. One comment per PR,
/// found by the hidden marker, keeps the conversation readable when a branch is pushed many times.
/// Only a marked comment written by the account <c>authorId</c> is updated (default 41898282, the account that acts for the
/// workflow <c>GITHUB_TOKEN</c>; user ids, unlike display names, cannot be reused). Without the filter,
/// anyone who pastes the marker into a comment would get it overwritten with the token's write access.
/// </summary>
public sealed partial class PullRequestCommenter(HttpClient http, Uri apiBase, string token, long authorId = PullRequestCommenter.ActionsTokenUserId)
{
    /// <summary>User id of the account that GitHub Actions uses for the workflow <c>GITHUB_TOKEN</c>.</summary>
    public const long ActionsTokenUserId = 41898282;

    public enum Outcome
    {
        Created,
        Updated,
    }

    public async Task<(Outcome Outcome, long CommentId)> UpsertAsync(string repository, int pullRequest, string body, CancellationToken ct = default)
    {
        if (!RepositoryName().IsMatch(repository))
        {
            throw new ArgumentException($"'{repository}' is not an owner/name repository.", nameof(repository));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequest);
        if (!body.Contains(MarkdownReport.Marker, StringComparison.Ordinal))
        {
            body = MarkdownReport.Marker + "\n" + body;
        }

        var existing = await FindExistingAsync(repository, pullRequest, ct);
        if (existing is { } id)
        {
            using var patch = Request(HttpMethod.Patch, $"repos/{repository}/issues/comments/{id}");
            patch.Content = JsonContent.Create(new CommentBody(body));
            using var response = await http.SendAsync(patch, ct);
            await EnsureSuccessAsync(response, "update the comment", ct);
            return (Outcome.Updated, id);
        }

        using var post = Request(HttpMethod.Post, $"repos/{repository}/issues/{pullRequest}/comments");
        post.Content = JsonContent.Create(new CommentBody(body));
        using var created = await http.SendAsync(post, ct);
        await EnsureSuccessAsync(created, "create the comment", ct);
        var comment = await created.Content.ReadFromJsonAsync<Comment>(ct) ?? throw new InvalidOperationException("Empty response from GitHub.");
        return (Outcome.Created, comment.Id);
    }

    private async Task<long?> FindExistingAsync(string repository, int pullRequest, CancellationToken ct)
    {
        // A busy PR can have hundreds of comments; GitHub pages at most 100 per request.
        for (var page = 1; page <= 30; page++)
        {
            using var request = Request(HttpMethod.Get, $"repos/{repository}/issues/{pullRequest}/comments?per_page=100&page={page}");
            using var response = await http.SendAsync(request, ct);
            await EnsureSuccessAsync(response, "list comments", ct);
            var comments = await response.Content.ReadFromJsonAsync<List<Comment>>(ct) ?? [];
            var mine = comments.LastOrDefault(c =>
                c.Body?.Contains(MarkdownReport.Marker, StringComparison.Ordinal) == true &&
                c.User?.Id == authorId);
            if (mine is not null)
            {
                return mine.Id;
            }

            if (comments.Count < 100)
            {
                return null;
            }
        }

        return null;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(apiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("plan-guard", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 300)
        {
            detail = detail[..300];
        }

        throw new HttpRequestException($"GitHub refused to {action}: {(int)response.StatusCode} {detail}", null, response.StatusCode);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryName();

    private sealed record CommentBody([property: JsonPropertyName("body")] string Body);

    private sealed record Comment(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("user")] CommentUser? User);

    private sealed record CommentUser([property: JsonPropertyName("id")] long Id);
}

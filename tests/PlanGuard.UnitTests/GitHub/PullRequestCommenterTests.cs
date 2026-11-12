using System.Net;
using System.Text;
using System.Text.Json;
using PlanGuard.GitHub;
using PlanGuard.Reporting;

namespace PlanGuard.UnitTests.GitHub;

public sealed class PullRequestCommenterTests
{
    /// <summary>In-memory stand-in for the GitHub REST API: records requests and answers from a script.</summary>
    private sealed class FakeGitHub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.ToString()));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static object Comment(long id, string body, long userId) => new { id, body, user = new { id = userId } };

    private static PullRequestCommenter Commenter(FakeGitHub fake) =>
        new(new HttpClient(fake), new Uri("https://api.github.test/"), "test-token-not-real");

    [Fact]
    public async Task Creates_a_comment_when_none_exists()
    {
        var fake = new FakeGitHub((req, _) => req.Method == HttpMethod.Get
            ? Json(new[] { Comment(1, "LGTM", 1001) })
            : Json(new { id = 77 }, HttpStatusCode.Created));

        var (outcome, id) = await Commenter(fake).UpsertAsync("acme/shop", 42, "## report", TestContext.Current.CancellationToken);

        outcome.Should().Be(PullRequestCommenter.Outcome.Created);
        id.Should().Be(77);
        fake.Requests.Should().HaveCount(2);
        fake.Requests[1].Method.Should().Be(HttpMethod.Post);
        fake.Requests[1].Path.Should().Be("/repos/acme/shop/issues/42/comments");
        fake.Requests[1].Body.Should().Contain(JsonEncodedText.Encode(MarkdownReport.Marker).ToString());
        fake.Requests[1].Auth.Should().Be("Bearer test-token-not-real");
    }

    [Fact]
    public async Task Updates_its_own_previous_comment()
    {
        var fake = new FakeGitHub((req, _) => req.Method == HttpMethod.Get
            ? Json(new[] { Comment(5, MarkdownReport.Marker + "\nold", PullRequestCommenter.ActionsTokenUserId) })
            : Json(new { id = 5 }));

        var (outcome, id) = await Commenter(fake).UpsertAsync("acme/shop", 42, MarkdownReport.Marker + "\nnew", TestContext.Current.CancellationToken);

        outcome.Should().Be(PullRequestCommenter.Outcome.Updated);
        id.Should().Be(5);
        fake.Requests[1].Method.Should().Be(HttpMethod.Patch);
        fake.Requests[1].Path.Should().Be("/repos/acme/shop/issues/comments/5");
    }

    [Fact]
    public async Task Never_overwrites_a_marked_comment_written_by_someone_else()
    {
        var fake = new FakeGitHub((req, _) => req.Method == HttpMethod.Get
            ? Json(new[] { Comment(9, MarkdownReport.Marker + " planted by a user", 666) })
            : Json(new { id = 10 }, HttpStatusCode.Created));

        var (outcome, _) = await Commenter(fake).UpsertAsync("acme/shop", 1, "body", TestContext.Current.CancellationToken);

        outcome.Should().Be(PullRequestCommenter.Outcome.Created);
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Pages_through_long_comment_threads()
    {
        var fake = new FakeGitHub((req, _) =>
        {
            if (req.Method != HttpMethod.Get)
            {
                return Json(new { id = 1 });
            }

            var page = req.RequestUri!.Query.Contains("page=1&", StringComparison.Ordinal) || req.RequestUri.Query.EndsWith("page=1", StringComparison.Ordinal) ? 1 : 2;
            return page == 1
                ? Json(Enumerable.Range(1, 100).Select(i => Comment(i, "chatter", 1002)).ToArray())
                : Json(new[] { Comment(500, MarkdownReport.Marker, PullRequestCommenter.ActionsTokenUserId) });
        });

        var (outcome, id) = await Commenter(fake).UpsertAsync("acme/shop", 3, "body", TestContext.Current.CancellationToken);

        outcome.Should().Be(PullRequestCommenter.Outcome.Updated);
        id.Should().Be(500);
        fake.Requests.Count(r => r.Method == HttpMethod.Get).Should().Be(2);
    }

    [Fact]
    public async Task Surfaces_api_errors_with_the_status_code()
    {
        var fake = new FakeGitHub((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"Resource not accessible by integration\"}") });

        var act = () => Commenter(fake).UpsertAsync("acme/shop", 3, "body", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("not-a-repo")]
    [InlineData("acme/shop/extra")]
    [InlineData("../etc/passwd")]
    public async Task Rejects_malformed_repository_names(string repo)
    {
        var fake = new FakeGitHub((_, _) => Json(Array.Empty<object>()));

        var act = () => Commenter(fake).UpsertAsync(repo, 1, "body", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }
}

using System.Net;
using System.Text;

namespace Archive.Ai.Tests;

/// <summary>
/// Stands in for a provider: canned answers out, every request kept for inspection.
/// </summary>
/// <remarks>
/// A hand-written handler rather than a mocking library, per the conventions in AGENTS.md. What
/// there is to check about a provider is the shape of the request it puts on the wire and what it
/// makes of the answer, and both need something on the other end.
/// </remarks>
internal sealed class FakeEndpoint : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

    private (HttpStatusCode Status, string Body) _last = (HttpStatusCode.OK, "{}");

    /// <summary>Every request, in order, with its body already read.</summary>
    internal List<(HttpMethod Method, Uri Uri, string Body, string? Authorization)> Requests { get; } = [];

    internal FakeEndpoint Answers(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _answers.Enqueue((status, body));

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A real transport checks first, and the retry policy turns on telling a cancellation
        // apart from a timeout — so a fake that answered a cancelled request would let a broken
        // policy pass.
        cancellationToken.ThrowIfCancellationRequested();

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add((
            request.Method,
            request.RequestUri!,
            body,
            request.Headers.Authorization?.ToString()));

        // Repeating the last answer rather than running dry keeps a retry test readable: the
        // point of it is how many attempts happened, not how many answers were queued.
        if (_answers.Count > 0)
        {
            _last = _answers.Dequeue();
        }

        var (status, response) = _last;

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        };
    }
}

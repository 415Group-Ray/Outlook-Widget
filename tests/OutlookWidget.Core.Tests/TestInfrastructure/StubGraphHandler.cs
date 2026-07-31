using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace OutlookWidget.Core.Tests.TestInfrastructure;

/// <summary>What one request carried, captured before the client disposes the message.</summary>
/// <param name="Uri">The absolute request URI, query string included.</param>
/// <param name="AuthorizationScheme">The scheme on the <c>Authorization</c> header, or null.</param>
/// <param name="HeaderNames">Every header name present on the request.</param>
internal sealed record CapturedRequest(
    string Uri,
    string? AuthorizationScheme,
    IReadOnlyList<string> HeaderNames);

/// <summary>
/// A Graph endpoint that answers from a script rather than from a network.
/// </summary>
/// <remarks>
/// <para>
/// The handler seam rather than a mocked client: request construction, header application, status
/// classification, response streaming, and JSON parsing are all things
/// <c>GraphMailClient</c> does, and substituting the client would exercise none of them.
/// </para>
/// <para>
/// It captures every request, so a test can assert what was <em>not</em> sent — no body property in a
/// <c>$select</c>, no <c>ConsistencyLevel</c> header — which is the only way to hold a constraint
/// that is defined by an absence.
/// </para>
/// </remarks>
internal sealed class StubGraphHandler : HttpMessageHandler
{
    private readonly List<(string Match, Func<HttpResponseMessage> Respond)> _routes = [];
    private readonly List<string> _hanging = [];

    /// <summary>Every request this handler saw.</summary>
    public ConcurrentBag<CapturedRequest> Requests { get; } = [];

    /// <summary>Answers any request whose URI contains <paramref name="match"/> with JSON.</summary>
    public StubGraphHandler Json(string match, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((match, () => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

        return this;
    }

    /// <summary>Answers with a status and no body, optionally carrying <c>Retry-After</c>.</summary>
    public StubGraphHandler Status(string match, HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        _routes.Add((match, () =>
        {
            var response = new HttpResponseMessage(status);

            if (retryAfter is { } delta)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
            }

            return response;
        }));

        return this;
    }

    /// <summary>Fails the request the way a dead connection does, before any response arrives.</summary>
    public StubGraphHandler NetworkFailure(string match)
    {
        _routes.Add((match, () => throw new HttpRequestException("simulated transport failure")));
        return this;
    }

    /// <summary>
    /// Answers with a success status and then dies while the body is being read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure <c>HttpCompletionOption.ResponseHeadersRead</c> creates and a before-the-response
    /// stub cannot reach: headers have arrived, the status is 200, and the connection drops
    /// mid-payload. Real .NET raises <c>HttpIOException</c> there, which derives from
    /// <see cref="IOException"/> and <em>not</em> from <see cref="HttpRequestException"/>.
    /// </para>
    /// <para>
    /// <b>It has to be a throwing stream, not a throwing <see cref="HttpContent"/>, and the first
    /// version of this stub got that wrong.</b> Overriding <c>SerializeToStreamAsync</c> to throw
    /// looks equivalent and is not: <see cref="HttpContent"/> wraps what escapes there in
    /// <see cref="HttpRequestException"/>, so the stub reproduced the case the client already handled
    /// and the test passed with the defect still present. <see cref="StreamContent"/> hands its inner
    /// stream back unbuffered, so the <see cref="IOException"/> surfaces raw inside
    /// <c>JsonDocument.ParseAsync</c> — which is where it surfaces in production.
    /// </para>
    /// </remarks>
    public StubGraphHandler BodyFailure(string match)
    {
        _routes.Add((match, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream()),
        }));

        return this;
    }

    /// <summary>
    /// Answers with an error status whose body read is cancelled, as a nested Graph deadline does
    /// after the response headers have arrived.
    /// </summary>
    public StubGraphHandler BodyCancellation(string match, HttpStatusCode status)
    {
        _routes.Add((match, () => new HttpResponseMessage(status)
        {
            Content = new StreamContent(new CancellingStream()),
        }));

        return this;
    }

    /// <summary>Never answers, so whichever bound the caller set is what ends the request.</summary>
    public StubGraphHandler Hang(string match)
    {
        _hanging.Add(match);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string uri = request.RequestUri!.ToString();

        Requests.Add(new CapturedRequest(
            uri,
            request.Headers.Authorization?.Scheme,
            [.. request.Headers.Select(header => header.Key)]));

        if (_hanging.Exists(match => uri.Contains(match, StringComparison.Ordinal)))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        foreach ((string match, Func<HttpResponseMessage> respond) in _routes)
        {
            if (uri.Contains(match, StringComparison.Ordinal))
            {
                return respond();
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A readable stream that dies on the first read, the way a dropped connection does.
    /// </summary>
    /// <remarks>
    /// Both read overloads throw, because which one the JSON reader reaches is an implementation
    /// detail of the parser rather than something this stub should depend on.
    /// </remarks>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated mid-response connection failure");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new IOException("simulated mid-response connection failure");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>A readable stream whose body read is ended by cancellation.</summary>
    private sealed class CancellingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new OperationCanceledException("simulated nested Graph deadline");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException("simulated nested Graph deadline");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

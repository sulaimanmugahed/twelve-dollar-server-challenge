using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

var sqlitePath = RequiredEnvironment("SQLITE_PATH");
var jwtSecret = Encoding.UTF8.GetBytes(RequiredEnvironment("JWT_SECRET"));
var host = Environment.GetEnvironmentVariable("HOST") ?? "127.0.0.1";
var port = ParsePort(Environment.GetEnvironmentVariable("PORT") ?? "3000");

if (!IPAddress.TryParse(host, out var bindAddress))
{
    throw new InvalidOperationException("HOST must be an IP address.");
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(75);
    options.Limits.MaxConcurrentConnections = 20_000;
    options.Listen(bindAddress, port, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
});

var database = new FeedDatabase(sqlitePath);
var startedAt = Stopwatch.GetTimestamp();
var app = builder.Build();
app.Lifetime.ApplicationStopping.Register(database.Dispose);

// Keep a single predictable JSON failure path rather than leaking framework error pages.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        await JsonResponses.ErrorAsync(context, StatusCodes.Status500InternalServerError, "internal server error");
    }
});

app.MapGet("/health", (HttpContext context) =>
{
    if (database.TryHealth(out var error))
    {
        var uptime = (long)Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        return JsonResponses.WriteAsync(context, StatusCodes.Status200OK, new HealthResponse("ok", "ok", uptime));
    }

    return JsonResponses.WriteAsync(
        context,
        StatusCodes.Status503ServiceUnavailable,
        new DegradedHealthResponse("degraded", "unreachable", error));
});

app.MapGet("/feed", (HttpContext context) =>
{
    var posts = database.GetFeed();
    return JsonResponses.WriteAsync(context, StatusCodes.Status200OK, new FeedResponse(posts));
});

app.MapGet("/posts/{postId}", async (HttpContext context, string postId) =>
{
    if (!PostIdentifier.TryParse(postId, out var parsed))
    {
        await JsonResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid post id");
        return;
    }

    var post = parsed.IsSqliteInteger ? database.GetPost(parsed.Value) : null;
    if (post is null)
    {
        await JsonResponses.ErrorAsync(context, StatusCodes.Status404NotFound, "post not found");
        return;
    }

    await JsonResponses.WriteAsync(context, StatusCodes.Status200OK, new PostResponse(post));
});

app.MapPost("/posts", async (HttpContext context) =>
{
    var auth = JwtAuthenticator.Authenticate(context.Request.Headers["Authorization"].ToString(), jwtSecret);
    if (auth.Status != AuthenticationStatus.Success)
    {
        await WriteAuthenticationError(context, auth.Status);
        return;
    }

    string? body;
    try
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        body = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("body", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
    catch (JsonException)
    {
        await JsonResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "malformed JSON body");
        return;
    }

    if (body is null)
    {
        await JsonResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "body is required");
        return;
    }

    body = body.Trim(JsWhitespace.Characters);
    if (body.Length == 0)
    {
        await JsonResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "body is required");
        return;
    }

    if (CountUnicodeScalars(body, 501) > 500)
    {
        await JsonResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "body must be at most 500 characters");
        return;
    }

    var post = database.CreatePost(auth.UserId, auth.Username!, body);
    await JsonResponses.WriteAsync(context, StatusCodes.Status201Created, new PostResponse(post));
});

app.MapPost("/posts/{postId}/like", async (HttpContext context, string postId) =>
{
    // Authentication deliberately precedes identifier validation; this is part of the API contract.
    var auth = JwtAuthenticator.Authenticate(context.Request.Headers["Authorization"].ToString(), jwtSecret);
    if (auth.Status != AuthenticationStatus.Success)
    {
        await WriteAuthenticationError(context, auth.Status);
        return;
    }

    if (!PostIdentifier.TryParse(postId, out var parsed))
    {
        await JsonResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid post id");
        return;
    }

    var alreadyLiked = false;
    if (!parsed.IsSqliteInteger || !database.TryLike(auth.UserId, parsed.Value, out alreadyLiked))
    {
        await JsonResponses.ErrorAsync(context, StatusCodes.Status404NotFound, "post not found");
        return;
    }

    await JsonResponses.WriteAsync(
        context,
        alreadyLiked ? StatusCodes.Status200OK : StatusCodes.Status201Created,
        new LikeResponse(true, alreadyLiked, parsed.Value));
});

// The challenge specifies a JSON 404 rather than ASP.NET Core's default empty fallback response.
app.MapFallback(context => JsonResponses.ErrorAsync(context, StatusCodes.Status404NotFound, "not found"));

await app.RunAsync();

static string RequiredEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");

static int ParsePort(string value) =>
    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
        ? port
        : throw new InvalidOperationException("PORT must be between 1 and 65535.");

static async Task WriteAuthenticationError(HttpContext context, AuthenticationStatus status)
{
    var message = status == AuthenticationStatus.MissingBearer
        ? "missing bearer token"
        : status == AuthenticationStatus.InvalidPayload
            ? "invalid token payload"
            : "invalid or expired token";
    await JsonResponses.ErrorAsync(context, StatusCodes.Status401Unauthorized, message);
}

static int CountUnicodeScalars(string value, int stopAfter)
{
    var count = 0;
    foreach (var _ in value.EnumerateRunes())
    {
        if (++count >= stopAfter)
        {
            return count;
        }
    }

    return count;
}

internal sealed class FeedDatabase : IDisposable
{
    private const string PostSelect = """
        SELECT p.id, p.body, p.created_at, u.username,
               (SELECT count(*) FROM likes l WHERE l.post_id = p.id) AS like_count
          FROM posts p JOIN users u ON u.id = p.user_id
        """;

    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private readonly SqliteCommand healthCommand;
    private readonly SqliteCommand feedCommand;
    private readonly SqliteCommand postCommand;
    private readonly SqliteParameter postIdParameter;
    private readonly SqliteCommand insertPostCommand;
    private readonly SqliteParameter postUserIdParameter;
    private readonly SqliteParameter postBodyParameter;
    private readonly SqliteCommand insertLikeCommand;
    private readonly SqliteParameter likeUserIdParameter;
    private readonly SqliteParameter likePostIdParameter;
    private readonly SqliteCommand postExistsCommand;
    private readonly SqliteParameter existsPostIdParameter;

    public FeedDatabase(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();

        connection = new SqliteConnection(connectionString);
        connection.Open();

        // WAL + NORMAL commits every completed write before its HTTP response while avoiding fsync twice.
        ExecutePragma("PRAGMA journal_mode = WAL");
        ExecutePragma("PRAGMA synchronous = NORMAL");
        ExecutePragma("PRAGMA busy_timeout = 5000");
        ExecutePragma("PRAGMA mmap_size = 1073741824");
        ExecutePragma("PRAGMA cache_size = -65536");
        ExecutePragma("PRAGMA temp_store = MEMORY");

        healthCommand = Prepare("SELECT 1");
        feedCommand = Prepare(PostSelect + " ORDER BY p.created_at DESC, p.id DESC LIMIT 20");

        postCommand = Prepare(PostSelect + " WHERE p.id = $postId");
        postIdParameter = AddIntegerParameter(postCommand, "$postId");

        insertPostCommand = Prepare("INSERT INTO posts (user_id, body) VALUES ($userId, $body) RETURNING id, created_at");
        postUserIdParameter = AddIntegerParameter(insertPostCommand, "$userId");
        postBodyParameter = insertPostCommand.CreateParameter();
        postBodyParameter.ParameterName = "$body";
        postBodyParameter.SqliteType = SqliteType.Text;
        insertPostCommand.Parameters.Add(postBodyParameter);

        // A single statement both establishes that the post exists and attempts the unique like insert.
        insertLikeCommand = Prepare("""
            INSERT INTO likes (user_id, post_id)
            SELECT $userId, $postId WHERE EXISTS (SELECT 1 FROM posts WHERE id = $postId)
            ON CONFLICT (user_id, post_id) DO NOTHING
            """);
        likeUserIdParameter = AddIntegerParameter(insertLikeCommand, "$userId");
        likePostIdParameter = AddIntegerParameter(insertLikeCommand, "$postId");

        postExistsCommand = Prepare("SELECT 1 FROM posts WHERE id = $postId");
        existsPostIdParameter = AddIntegerParameter(postExistsCommand, "$postId");

        // Parameters must be attached before preparing. This caches plans, never query results.
        healthCommand.Prepare();
        feedCommand.Prepare();
        postCommand.Prepare();
        insertPostCommand.Prepare();
        insertLikeCommand.Prepare();
        postExistsCommand.Prepare();
    }

    public bool TryHealth(out string error)
    {
        try
        {
            lock (gate)
            {
                _ = healthCommand.ExecuteScalar();
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public IReadOnlyList<PostResponseItem> GetFeed()
    {
        lock (gate)
        {
            var posts = new List<PostResponseItem>(20);
            using var reader = feedCommand.ExecuteReader();
            while (reader.Read())
            {
                posts.Add(ReadPost(reader));
            }

            return posts;
        }
    }

    public PostResponseItem? GetPost(long postId)
    {
        lock (gate)
        {
            postIdParameter.Value = postId;
            using var reader = postCommand.ExecuteReader();
            return reader.Read() ? ReadPost(reader) : null;
        }
    }

    public PostResponseItem CreatePost(long userId, string username, string body)
    {
        lock (gate)
        {
            postUserIdParameter.Value = userId;
            postBodyParameter.Value = body;
            using var reader = insertPostCommand.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Post insert returned no row.");
            }

            // Finishing and disposing RETURNING is what completes SQLite's implicit write transaction.
            var postId = reader.GetInt64(0);
            var createdAt = reader.GetString(1);
            return new PostResponseItem(postId, body, createdAt, username, 0);
        }
    }

    public bool TryLike(long userId, long postId, out bool alreadyLiked)
    {
        lock (gate)
        {
            likeUserIdParameter.Value = userId;
            likePostIdParameter.Value = postId;
            if (insertLikeCommand.ExecuteNonQuery() == 1)
            {
                alreadyLiked = false;
                return true;
            }

            existsPostIdParameter.Value = postId;
            if (postExistsCommand.ExecuteScalar() is not null)
            {
                alreadyLiked = true;
                return true;
            }

            alreadyLiked = false;
            return false;
        }
    }

    public void Dispose()
    {
        healthCommand.Dispose();
        feedCommand.Dispose();
        postCommand.Dispose();
        insertPostCommand.Dispose();
        insertLikeCommand.Dispose();
        postExistsCommand.Dispose();
        connection.Dispose();
    }

    private void ExecutePragma(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteCommand Prepare(string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static SqliteParameter AddIntegerParameter(SqliteCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.SqliteType = SqliteType.Integer;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static PostResponseItem ReadPost(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4));
}

internal static class JwtAuthenticator
{
    public static AuthenticationResult Authenticate(string authorization, byte[] secret)
    {
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return new(AuthenticationStatus.MissingBearer, 0, null);
        }

        var token = authorization[7..];
        var firstDot = token.IndexOf('.');
        var secondDot = firstDot < 0 ? -1 : token.IndexOf('.', firstDot + 1);
        if (firstDot <= 0 || secondDot <= firstDot + 1 || secondDot == token.Length - 1 || token.IndexOf('.', secondDot + 1) >= 0)
        {
            return new(AuthenticationStatus.InvalidToken, 0, null);
        }

        var headerSegment = token[..firstDot];
        var payloadSegment = token[(firstDot + 1)..secondDot];
        var signatureSegment = token[(secondDot + 1)..];
        if (!TryBase64UrlDecode(headerSegment, out var headerBytes)
            || !TryBase64UrlDecode(payloadSegment, out var payloadBytes)
            || !TryBase64UrlDecode(signatureSegment, out _))
        {
            return new(AuthenticationStatus.InvalidToken, 0, null);
        }

        var signedText = Encoding.ASCII.GetBytes($"{headerSegment}.{payloadSegment}");
        var digest = HMACSHA256.HashData(secret, signedText);
        var expectedSignature = Base64UrlEncode(digest);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedSignature),
                Encoding.ASCII.GetBytes(signatureSegment)))
        {
            return new(AuthenticationStatus.InvalidToken, 0, null);
        }

        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            if (header.RootElement.ValueKind != JsonValueKind.Object
                || !header.RootElement.TryGetProperty("alg", out var algorithm)
                || algorithm.ValueKind != JsonValueKind.String
                || algorithm.GetString() != "HS256")
            {
                return new(AuthenticationStatus.InvalidToken, 0, null);
            }

            using var payload = JsonDocument.Parse(payloadBytes);
            if (payload.RootElement.ValueKind != JsonValueKind.Object || !ValidateTimes(payload.RootElement))
            {
                return new(AuthenticationStatus.InvalidToken, 0, null);
            }

            if (!payload.RootElement.TryGetProperty("sub", out var subject)
                || subject.ValueKind != JsonValueKind.String
                || !TryParsePositiveInt64(subject.GetString(), out var userId)
                || !payload.RootElement.TryGetProperty("username", out var username)
                || username.ValueKind != JsonValueKind.String)
            {
                return new(AuthenticationStatus.InvalidPayload, 0, null);
            }

            return new(AuthenticationStatus.Success, userId, username.GetString());
        }
        catch (JsonException)
        {
            return new(AuthenticationStatus.InvalidToken, 0, null);
        }
    }

    private static bool ValidateTimes(JsonElement payload)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (payload.TryGetProperty("exp", out var expiration))
        {
            if (expiration.ValueKind != JsonValueKind.Number
                || !expiration.TryGetDouble(out var expiresAt)
                || double.IsNaN(expiresAt)
                || double.IsInfinity(expiresAt)
                || now >= expiresAt)
            {
                return false;
            }
        }

        if (payload.TryGetProperty("nbf", out var notBefore))
        {
            if (notBefore.ValueKind != JsonValueKind.Number
                || !notBefore.TryGetDouble(out var notBeforeAt)
                || double.IsNaN(notBeforeAt)
                || double.IsInfinity(notBeforeAt)
                || now < notBeforeAt)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value.Length == 0 || value.Length % 4 == 1)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryParsePositiveInt64(string? value, out long number)
    {
        number = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var anyNonZero = false;
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            anyNonZero |= character != '0';
        }

        return anyNonZero && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }
}

internal readonly record struct PostIdentifier(bool IsSqliteInteger, long Value)
{
    public static bool TryParse(string value, out PostIdentifier identifier)
    {
        identifier = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var anyNonZero = false;
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            anyNonZero |= character != '0';
        }

        if (!anyNonZero)
        {
            return false;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            // It is syntactically a positive decimal id, but outside SQLite's signed INTEGER range.
            identifier = new(false, 0);
            return true;
        }

        identifier = new(true, parsed);
        return true;
    }
}

internal static class JsWhitespace
{
    // ECMAScript's String.prototype.trim whitespace and line terminator code points.
    public static readonly char[] Characters = ['\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u0020', '\u00A0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF'];
}

internal enum AuthenticationStatus
{
    Success,
    MissingBearer,
    InvalidToken,
    InvalidPayload,
}

internal readonly record struct AuthenticationResult(AuthenticationStatus Status, long UserId, string? Username);

internal static class JsonResponses
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static Task ErrorAsync(HttpContext context, int statusCode, string message) =>
        WriteAsync(context, statusCode, new ErrorResponse(message));

    public static Task WriteAsync<T>(HttpContext context, int statusCode, T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = json.Length;
        return context.Response.Body.WriteAsync(json, 0, json.Length, context.RequestAborted);
    }
}

internal sealed class ErrorResponse(string error)
{
    [JsonPropertyName("error"), JsonPropertyOrder(0)]
    public string Error { get; } = error;
}

internal sealed class HealthResponse(string status, string database, long uptimeSeconds)
{
    [JsonPropertyName("status"), JsonPropertyOrder(0)]
    public string Status { get; } = status;

    [JsonPropertyName("db"), JsonPropertyOrder(1)]
    public string Database { get; } = database;

    [JsonPropertyName("uptime_s"), JsonPropertyOrder(2)]
    public long UptimeSeconds { get; } = uptimeSeconds;
}

internal sealed class DegradedHealthResponse(string status, string database, string error)
{
    [JsonPropertyName("status"), JsonPropertyOrder(0)]
    public string Status { get; } = status;

    [JsonPropertyName("db"), JsonPropertyOrder(1)]
    public string Database { get; } = database;

    [JsonPropertyName("error"), JsonPropertyOrder(2)]
    public string Error { get; } = error;
}

internal sealed class FeedResponse(IReadOnlyList<PostResponseItem> posts)
{
    [JsonPropertyName("posts"), JsonPropertyOrder(0)]
    public IReadOnlyList<PostResponseItem> Posts { get; } = posts;
}

internal sealed class PostResponse(PostResponseItem post)
{
    [JsonPropertyName("post"), JsonPropertyOrder(0)]
    public PostResponseItem Post { get; } = post;
}

internal sealed class PostResponseItem(long id, string body, string createdAt, string author, long likeCount)
{
    [JsonPropertyName("id"), JsonPropertyOrder(0)]
    public long Id { get; } = id;

    [JsonPropertyName("body"), JsonPropertyOrder(1)]
    public string Body { get; } = body;

    [JsonPropertyName("created_at"), JsonPropertyOrder(2)]
    public string CreatedAt { get; } = createdAt;

    [JsonPropertyName("author"), JsonPropertyOrder(3)]
    public string Author { get; } = author;

    [JsonPropertyName("like_count"), JsonPropertyOrder(4)]
    public long LikeCount { get; } = likeCount;
}

internal sealed class LikeResponse(bool liked, bool alreadyLiked, long postId)
{
    [JsonPropertyName("liked"), JsonPropertyOrder(0)]
    public bool Liked { get; } = liked;

    [JsonPropertyName("already_liked"), JsonPropertyOrder(1)]
    public bool AlreadyLiked { get; } = alreadyLiked;

    [JsonPropertyName("post_id"), JsonPropertyOrder(2)]
    public long PostId { get; } = postId;
}

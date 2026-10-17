using System.Text.Json;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.BedrockAgentCoreControl;
using Amazon.Runtime;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Region/credentials come from the "AWS" config section, env vars, or the host's role.
var awsOptions = builder.Configuration.GetAWSOptions();
var accessKeyId = builder.Configuration["AWS:AccessKeyId"];
var secretAccessKey = builder.Configuration["AWS:SecretAccessKey"];
if (!string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey))
{
    // Explicit static keys (local dev only); a session token is needed for temporary credentials.
    var sessionToken = builder.Configuration["AWS:SessionToken"];
    awsOptions.Credentials = string.IsNullOrEmpty(sessionToken)
        ? new BasicAWSCredentials(accessKeyId, secretAccessKey)
        : new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken);
}
builder.Services.AddDefaultAWSOptions(awsOptions);
builder.Services.AddAWSService<IAmazonBedrockAgentCoreControl>();
builder.Services.AddAWSService<IAmazonBedrockAgentCore>();

builder.Services.Configure<AgentRuntimeOptions>(builder.Configuration.GetSection("AgentRuntime"));
builder.Services.AddSingleton<AgentRuntimeRegistry>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Dev-only API docs: /openapi/v1.json and the Scalar UI at /scalar.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
}

// Registers (or updates) the runtime if needed and returns its identity.
app.MapGet("/runtime", (AgentRuntimeRegistry registry, CancellationToken ct) =>
    registry.GetOrRegisterAsync(ct));

// Spawns a new agent session. AgentCore starts the session's microVM on its first invocation,
// so an optional initial prompt is sent right away; without one only the session id is allocated.
app.MapPost("/sessions", async (
    CreateSessionRequest? request,
    AgentRuntimeRegistry registry,
    IAmazonBedrockAgentCore agentCore,
    CancellationToken ct) =>
{
    var runtime = await registry.GetOrRegisterAsync(ct);
    var sessionId = NewSessionId();

    if (string.IsNullOrWhiteSpace(request?.Prompt))
    {
        return Results.Created($"/sessions/{sessionId}", new SessionResponse(sessionId, null));
    }

    var response = await InvokeAsync(agentCore, runtime, sessionId, request.Prompt, ct);
    return Results.Created($"/sessions/{sessionId}", new SessionResponse(sessionId, response));
});

// Non-streaming turn in an existing session. Body: { "prompt": "..." }
app.MapPost("/sessions/{sessionId}/invocations", async (
    string sessionId,
    PromptRequest request,
    AgentRuntimeRegistry registry,
    IAmazonBedrockAgentCore agentCore,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest(new { error = "'prompt' is required." });
    }

    if (!IsValidSessionId(sessionId))
    {
        return Results.BadRequest(new { error = "Session ids must be at least 33 characters." });
    }

    var runtime = await registry.GetOrRegisterAsync(ct);
    var response = await InvokeAsync(agentCore, runtime, sessionId, request.Prompt, ct);
    return Results.Ok(new SessionResponse(sessionId, response));
});

// Streaming turn: forwards an AG-UI RunAgentInput and relays the runtime's AG-UI event stream.
app.MapPost("/sessions/{sessionId}/agui", async (
    string sessionId,
    HttpContext http,
    AgentRuntimeRegistry registry,
    IAmazonBedrockAgentCore agentCore,
    CancellationToken ct) =>
{
    if (!IsValidSessionId(sessionId))
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var runtime = await registry.GetOrRegisterAsync(ct);

    var payload = new MemoryStream();
    await http.Request.Body.CopyToAsync(payload, ct);
    payload.Position = 0;

    using var result = await agentCore.InvokeAgentRuntimeAsync(new InvokeAgentRuntimeRequest
    {
        AgentRuntimeArn = runtime.Arn,
        RuntimeSessionId = sessionId,
        ContentType = "application/json",
        Accept = "text/event-stream",
        Payload = payload,
    }, ct);

    http.Response.ContentType = result.ContentType ?? "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    // Flush per chunk so events reach the client as they arrive.
    var buffer = new byte[8192];
    int read;
    while ((read = await result.Response.ReadAsync(buffer, ct)) > 0)
    {
        await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
        await http.Response.Body.FlushAsync(ct);
    }
})
.Accepts<JsonElement>("application/json")
.Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

// Terminates the session's microVM.
app.MapDelete("/sessions/{sessionId}", async (
    string sessionId,
    AgentRuntimeRegistry registry,
    IAmazonBedrockAgentCore agentCore,
    CancellationToken ct) =>
{
    var runtime = await registry.GetOrRegisterAsync(ct);
    try
    {
        await agentCore.StopRuntimeSessionAsync(new StopRuntimeSessionRequest
        {
            AgentRuntimeArn = runtime.Arn,
            RuntimeSessionId = sessionId,
            ClientToken = Guid.NewGuid().ToString(),
        }, ct);
    }
    catch (Amazon.BedrockAgentCore.Model.ResourceNotFoundException)
    {
        return Results.NotFound();
    }

    return Results.NoContent();
});

app.Run();

// AgentCore requires runtime session ids of at least 33 characters; a dashed GUID has 36.
static string NewSessionId() => Guid.NewGuid().ToString();

static bool IsValidSessionId(string sessionId) => sessionId.Length >= 33;

static async Task<string?> InvokeAsync(
    IAmazonBedrockAgentCore agentCore, RegisteredAgentRuntime runtime, string sessionId, string prompt, CancellationToken ct)
{
    var payload = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new PromptRequest(prompt), JsonSerializerOptions.Web));

    using var result = await agentCore.InvokeAgentRuntimeAsync(new InvokeAgentRuntimeRequest
    {
        AgentRuntimeArn = runtime.Arn,
        RuntimeSessionId = sessionId,
        ContentType = "application/json",
        Accept = "application/json",
        Payload = payload,
    }, ct);

    var body = await JsonSerializer.DeserializeAsync<RuntimeResponse>(result.Response, JsonSerializerOptions.Web, ct);
    return body?.Response;
}

internal sealed record CreateSessionRequest(string? Prompt);

internal sealed record PromptRequest(string? Prompt);

internal sealed record SessionResponse(string SessionId, string? Response);

// Shape of the AgentRuntime's non-streaming /invocations response.
internal sealed record RuntimeResponse(string? Response, string? SessionId);

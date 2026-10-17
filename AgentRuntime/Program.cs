using System.Collections.Concurrent;
using System.ComponentModel;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Microsoft.Agents.AI;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Bedrock client: region/credentials come from the "AWS" config section, env vars,
// or the execution role when running inside AgentCore Runtime.
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
builder.Services.AddAWSService<IAmazonBedrockRuntime>();

builder.Services.AddAGUIServer();
builder.Services.AddOpenApi();

builder.Services.AddSingleton<AIAgent>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var modelId = config["Bedrock:ModelId"]
        ?? throw new InvalidOperationException("Bedrock:ModelId is not configured.");

    return sp.GetRequiredService<IAmazonBedrockRuntime>()
        .AsIChatClient(modelId)
        .AsAIAgent(
            name: config["Agent:Name"] ?? "AgentCoreMafAgent",
            instructions: config["Agent:Instructions"] ?? "You are a helpful assistant. Answer concisely.",
            tools: [AIFunctionFactory.Create(GetCurrentDate)],
            loggerFactory: sp.GetRequiredService<ILoggerFactory>(),
            services: sp);
});

// AgentCore Runtime pins a session to one microVM, so in-memory session state is sufficient here.
builder.Services.AddSingleton<ConcurrentDictionary<string, AgentSession>>();

var app = builder.Build();

const string SessionIdHeader = "X-Amzn-Bedrock-AgentCore-Runtime-Session-Id";
const string AguiPath = "/invocations/agui";

// AgentCore only calls /invocations, so streaming requests are routed to the AG-UI endpoint.
app.Use((http, next) =>
{
    if (HttpMethods.IsPost(http.Request.Method)
        && http.Request.Path.Equals("/invocations", StringComparison.OrdinalIgnoreCase)
        && http.Request.Headers.Accept.Any(a => a?.Contains("text/event-stream") == true))
    {
        http.Request.Path = AguiPath;
    }

    return next(http);
});
app.UseRouting();

// Dev-only API docs: /openapi/v1.json and the Scalar UI at /scalar.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
}

// AgentCore health check. time_of_last_update is omitted on purpose: a value that advances on
// every ping keeps the session from ever idling out (sessions then live until MaxLifetime).
app.MapGet("/ping", () => Results.Ok(new { status = "Healthy" }));

// Streaming path: AG-UI protocol (RunAgentInput in, AG-UI SSE events out). Unlike MapAGUIServer,
// history is kept server side (shared with the non-streaming path), so clients send only new messages.
app.MapPost(AguiPath, async (
    HttpContext http,
    RunAgentInput input,
    AIAgent agent,
    ConcurrentDictionary<string, AgentSession> sessions,
    IOptions<JsonOptions> jsonOptions,
    CancellationToken ct) =>
{
    var context = input.ToChatRequestContext(jsonOptions.Value.SerializerOptions, null);
    var sessionId = http.Request.Headers[SessionIdHeader].FirstOrDefault()
        ?? (string.IsNullOrWhiteSpace(input.ThreadId) ? Guid.NewGuid().ToString() : input.ThreadId);
    var session = await GetOrCreateSessionAsync(sessions, agent, sessionId, ct);

    http.Response.Headers[SessionIdHeader] = sessionId;

    var options = new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions };
    return TypedResults.ServerSentEvents(agent
        .RunStreamingAsync(context.Messages, session, options, ct)
        .AsChatResponseUpdatesAsync()
        .AsAGUIEventStreamAsync(context, ct));
});

// Non-streaming path. Body: { "prompt": "..." }
app.MapPost("/invocations", async (
    HttpContext http,
    InvocationRequest request,
    AIAgent agent,
    ConcurrentDictionary<string, AgentSession> sessions,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest(new { error = "'prompt' is required." });
    }

    var sessionId = http.Request.Headers[SessionIdHeader].FirstOrDefault() ?? Guid.NewGuid().ToString();
    var session = await GetOrCreateSessionAsync(sessions, agent, sessionId, ct);

    http.Response.Headers[SessionIdHeader] = sessionId;

    var response = await agent.RunAsync(request.Prompt, session, cancellationToken: ct);
    return Results.Ok(new InvocationResponse(response.Text, sessionId));
});

app.Run();

static async Task<AgentSession> GetOrCreateSessionAsync(
    ConcurrentDictionary<string, AgentSession> sessions, AIAgent agent, string sessionId, CancellationToken ct) =>
    sessions.TryGetValue(sessionId, out var session)
        ? session
        : sessions.GetOrAdd(sessionId, await agent.CreateSessionAsync(ct));

[Description("Gets the current date and time in UTC (ISO 8601).")]
static string GetCurrentDate() => DateTimeOffset.UtcNow.ToString("O");

internal sealed record InvocationRequest(string? Prompt);

internal sealed record InvocationResponse(string Response, string SessionId);

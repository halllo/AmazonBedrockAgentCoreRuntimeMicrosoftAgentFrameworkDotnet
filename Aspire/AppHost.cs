var builder = DistributedApplication.CreateBuilder(args);

// The agent container's app, run locally (ports come from each project's launchSettings.json).
builder.AddProject<Projects.AgentRuntime>("agent-runtime")
    .WithHttpHealthCheck("/ping")
    .WithUrl("/scalar", "Scalar");

// Registers the runtime in AgentCore and spawns sessions there (not against the local agent-runtime).
builder.AddProject<Projects.AgentManagement>("agent-management")
    .WithUrl("/scalar", "Scalar");

builder.Build().Run();

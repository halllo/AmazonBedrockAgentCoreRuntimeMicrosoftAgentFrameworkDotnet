var builder = DistributedApplication.CreateBuilder(args);

// The agent container's app, run locally (ports come from each project's launchSettings.json).
builder.AddProject<Projects.AgentRuntime>("agent-runtime")
    .WithHttpHealthCheck("/ping")
    .WithUrl("/scalar", "Scalar");

// Registers the runtime in AgentCore and spawns sessions there (not against the local agent-runtime).
var management = builder.AddProject<Projects.AgentManagement>("agent-management")
    .WithUrl("/scalar", "Scalar");

// Angular UI (ng serve on :4200); its dev-server proxy forwards /api to agent-management.
builder.AddJavaScriptApp("agent-management-ui", "../AgentManagementUi", "start")
    .WithReference(management)
    .WaitFor(management)
    .WithHttpEndpoint(port: 4200, isProxied: false);

builder.Build().Run();

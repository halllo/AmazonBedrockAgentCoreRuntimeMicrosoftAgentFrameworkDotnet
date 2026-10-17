# Amazon Bedrock AgentCore Runtime & Microsoft Agent Framework (dotnet)

Experimental: a Microsoft Agent Framework agent hosted in Amazon Bedrock AgentCore Runtime.

```text
  client (Scalar, AG-UI)
         |
         v
+-------------------------------+         +--------------------------------+
| AgentManagement :5080         |         | AgentRuntime :8080 (local dev) |
| GET /runtime, /sessions/...   |         | same code as the container     |
+-------------------------------+         +--------------------------------+
   |                       |                    Aspire starts both locally
   | control plane         | data plane
   | Create/Update/Get     | InvokeAgentRuntime, StopRuntimeSession
   | AgentRuntime          | (runtimeSessionId)
   v                       v
+---------------------------------------------------------------- AWS -----+
| AgentCore Runtime "AgentRuntimeMAF" (DEFAULT -> latest version)          |
|                                                                          |
|   session A --> microVM [ AgentRuntime container :8080 ] --+             |
|   session B --> microVM [ AgentRuntime container :8080 ] --+--> Bedrock  |
|                    ^       (one microVM per session,                     |
|                    |        history kept in memory)                      |
|                    +---- image pulled from ECR (agentcore-maf:<tag>)     |
+--------------------------------------------------------------------------+
```

- `AgentRuntime` – the agent (Bedrock LLM, `GetCurrentDate` tool). Container image for AgentCore (`Dockerfile`, linux/arm64, port 8080).
- `AgentManagement` – registers the runtime in AgentCore and spawns sessions.
- `Aspire` – starts both locally.

## Run

```sh
cd AgentRuntime   # both projects share this UserSecretsId
dotnet user-secrets set "AWS:Profile" "<profile>"   # or AWS:AccessKeyId / AWS:SecretAccessKey (/ AWS:SessionToken)
cd ..
dotnet run --project Aspire
```

AgentRuntime: <http://localhost:8080> · AgentManagement: <http://localhost:5080> (both redirect to Scalar in Development).

## AgentRuntime

Speaks the AgentCore HTTP protocol:

- `GET /ping` – health. Leaves out `time_of_last_update`, so idle sessions can time out.
- `POST /invocations` `{"prompt":"..."}` → `{"response","sessionId"}`. The conversation is kept in memory per `X-Amzn-Bedrock-AgentCore-Runtime-Session-Id`.
- `POST /invocations` with `Accept: text/event-stream` – [AG-UI](https://docs.ag-ui.com) streaming. It shares the session history with the non-streaming path, so clients send only new messages (the session falls back to `threadId` when the header is missing).

Config: `AWS:Region` (`eu-central-1`), `Bedrock:ModelId`, `Agent:Name`, `Agent:Instructions`.

## AgentManagement

On first use it looks up the runtime `AgentRuntime:Name`. It creates the runtime if missing, updates it if its config changed, otherwise reuses it, and then waits for `READY`. A session starts on its first invocation; session ids must be at least 33 characters.

- `GET /runtime` – register if needed and return id/arn/version
- `POST /sessions` – new session, optional `{"prompt"}`
- `POST /sessions/{id}/invocations` – `{"prompt"}`
- `POST /sessions/{id}/agui` – AG-UI stream
- `GET /sessions` – sessions this instance invoked that haven't expired yet, with their runtime version. AgentCore has no API for listing sessions, so this is tracked in memory and lost on restart.
- `DELETE /sessions/{id}` – stop the session

Config: `AgentRuntime:ContainerUri` and `AgentRuntime:RoleArn` (both required), plus `NetworkMode`, `EnvironmentVariables`, `IdleSessionTimeout` (15 min) and `MaxSessionLifetime` (8 h). Config changes are picked up without a restart.

## Deploy to AgentCore

```sh
REGION=eu-central-1
ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
REPO=agentcore-maf
IMAGE=$ACCOUNT.dkr.ecr.$REGION.amazonaws.com/${REPO}:latest

# 1. ECR repository + image (AgentCore requires linux/arm64)
aws ecr create-repository --repository-name $REPO --region $REGION
aws ecr get-login-password --region $REGION | podman login --username AWS --password-stdin $ACCOUNT.dkr.ecr.$REGION.amazonaws.com
podman build --platform linux/arm64 --format docker -t $IMAGE AgentRuntime
podman push $IMAGE

# 2. Execution role assumed by the runtime
aws iam create-role --role-name AgentCoreMafRuntime --assume-role-policy-document '{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Service": "bedrock-agentcore.amazonaws.com" },
    "Action": "sts:AssumeRole",
    "Condition": {
      "StringEquals": { "aws:SourceAccount": "'$ACCOUNT'" },
      "ArnLike": { "aws:SourceArn": "arn:aws:bedrock-agentcore:'$REGION':'$ACCOUNT':*" }
    }
  }]
}'
aws iam put-role-policy --role-name AgentCoreMafRuntime --policy-name runtime --policy-document '{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer"],
      "Resource": "arn:aws:ecr:'$REGION':'$ACCOUNT':repository/'$REPO'" },
    { "Effect": "Allow", "Action": "ecr:GetAuthorizationToken", "Resource": "*" },
    { "Effect": "Allow", "Action": ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams", "logs:DescribeLogGroups"],
      "Resource": "arn:aws:logs:'$REGION':'$ACCOUNT':log-group:/aws/bedrock-agentcore/runtimes/*" },
    { "Effect": "Allow", "Action": ["xray:PutTraceSegments", "xray:PutTelemetryRecords", "cloudwatch:PutMetricData"], "Resource": "*" },
    { "Effect": "Allow", "Action": ["bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream"],
      "Resource": ["arn:aws:bedrock:*::foundation-model/*", "arn:aws:bedrock:*:'$ACCOUNT':inference-profile/*"] }
  ]
}'

# 3. Permissions for AgentManagement's IAM user (run as admin). The managed policy doesn't allow passing this role, hence the extra PassRole policy.
MGMT_USER=agents-experiments
aws iam attach-user-policy --user-name $MGMT_USER --policy-arn arn:aws:iam::aws:policy/BedrockAgentCoreFullAccess
aws iam put-user-policy --user-name $MGMT_USER --policy-name pass-agentcore-runtime-role --policy-document '{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": "iam:PassRole",
    "Resource": "arn:aws:iam::'$ACCOUNT':role/AgentCoreMafRuntime",
    "Condition": { "StringEquals": { "iam:PassedToService": "bedrock-agentcore.amazonaws.com" } }
  }]
}'

# 4. Point AgentManagement at both; the runtime is created on its first request (e.g. GET /runtime)
cd AgentRuntime   # shared UserSecretsId
dotnet user-secrets set "AgentRuntime:ContainerUri" "$IMAGE"
dotnet user-secrets set "AgentRuntime:RoleArn" "arn:aws:iam::${ACCOUNT}:role/AgentCoreMafRuntime"
```

## Update the agent

AgentManagement only updates the runtime when its config changes, so pushing to an existing tag doesn't roll anything out. Push under a new tag instead:

```sh
TAG=$(date +%Y%m%d-%H%M%S)
IMAGE=$ACCOUNT.dkr.ecr.$REGION.amazonaws.com/${REPO}:${TAG}
podman build --platform linux/arm64 --format docker -t $IMAGE AgentRuntime
podman push $IMAGE
cd AgentRuntime && dotnet user-secrets set "AgentRuntime:ContainerUri" "$IMAGE" && cd ..
curl localhost:5080/runtime   # UpdateAgentRuntime -> new version, waits for READY
```

AgentManagement doesn't need a restart. The `DEFAULT` endpoint switches to the new version, so new sessions run the new image. Running sessions keep their microVM until they end; `GET /sessions` shows which version each one runs, and `DELETE /sessions/{id}` stops one so it restarts on the new version.

Changes to `appsettings.json` only (instructions, model id) also need a new image. Settings passed through `AgentRuntime:EnvironmentVariables` (for example `Agent__Instructions`) only need the secret changed.

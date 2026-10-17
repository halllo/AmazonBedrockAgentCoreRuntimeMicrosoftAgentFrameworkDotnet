# Agent Core Runtime & Microsoft Agent Framework (dotnet)

Experimental: a Microsoft Agent Framework agent hosted in Amazon Bedrock AgentCore Runtime.

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
- `DELETE /sessions/{id}` – stop the session

Config: `AgentRuntime:ContainerUri` and `AgentRuntime:RoleArn` (both required), plus `NetworkMode` and `EnvironmentVariables`.

## Deploy to AgentCore

```sh
REGION=eu-central-1
ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
REPO=agentcore-maf
IMAGE=$ACCOUNT.dkr.ecr.$REGION.amazonaws.com/$REPO:latest

# 1. ECR repository + image (AgentCore requires linux/arm64)
aws ecr create-repository --repository-name $REPO --region $REGION
aws ecr get-login-password --region $REGION | docker login --username AWS --password-stdin $ACCOUNT.dkr.ecr.$REGION.amazonaws.com
docker buildx build --platform linux/arm64 -t $IMAGE --push AgentRuntime

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

# 3. Point AgentManagement at both; the runtime is created on its first request (e.g. GET /runtime)
cd AgentRuntime   # shared UserSecretsId
dotnet user-secrets set "AgentRuntime:ContainerUri" "$IMAGE"
dotnet user-secrets set "AgentRuntime:RoleArn" "arn:aws:iam::$ACCOUNT:role/AgentCoreMafRuntime"
```

AgentManagement's identity needs `bedrock-agentcore:CreateAgentRuntime`, `UpdateAgentRuntime`, `GetAgentRuntime`, `ListAgentRuntimes`, `InvokeAgentRuntime`, `StopRuntimeSession`, plus `iam:PassRole` on the role.

AgentManagement only updates the runtime when its config changes, so pushing a new image under the same tag doesn't roll it out. Push a new tag (for example a git sha) and update `AgentRuntime:ContainerUri`.

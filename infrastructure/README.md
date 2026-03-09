# NorthStar Infrastructure

AWS CDK infrastructure for deploying NorthStar.Api to AWS Fargate.

**Note:** This is the infrastructure directory within the NorthStar.Api monorepo.

## Architecture

- **ECS Fargate**: Runs the containerized NorthStar.Api (0.25 vCPU, 0.5 GB RAM)
- **Application Load Balancer**: Public HTTP endpoint
- **ECR**: Docker image repository
- **Secrets Manager**: Securely stores Polestar credentials
- **Auto-scaling**: Scales between 1-2 tasks based on CPU usage

## Prerequisites

1. **AWS CLI** configured with credentials:
   ```bash
   aws configure
   ```

2. **AWS CDK** installed (already installed - v2.1022.0)

3. **Docker** installed and running

4. **.NET 8 SDK** installed

## Cost Estimate

**Free Tier Eligible:**
- ECR: 500 MB storage (free tier)
- Fargate: 20 GB-Hours compute + 10 GB ephemeral storage (free tier)
- CloudWatch Logs: 5 GB ingestion (free tier)

**Costs After Free Tier:**
- Application Load Balancer: ~$16/month (not free tier eligible)
- Fargate (if exceeds free tier): ~$0.04/hour = ~$30/month for 24/7
- Secrets Manager: $0.40/secret/month

**Estimated monthly cost: $16-50 depending on usage**

## Quick Start

### Automated Deployment

```bash
chmod +x deploy.sh
./deploy.sh
```

This script will:
1. Bootstrap CDK (first time only)
2. Deploy infrastructure
3. Build Docker image
4. Push to ECR
5. Update ECS service

## Manual Deployment

### 1. Bootstrap CDK (first time only)

```bash
cdk bootstrap
```

### 2. Deploy infrastructure

```bash
dotnet build src
cdk deploy
```

### 3. Build and push Docker image

```bash
# Get ECR URI from stack output
ECR_URI=$(aws cloudformation describe-stacks \
  --stack-name NorthStarInfrastructureStack \
  --query "Stacks[0].Outputs[?OutputKey=='RepositoryUri'].OutputValue" \
  --output text)

# Build and push (from repo root)
cd ..
docker build -t northstar-api:latest .
docker tag northstar-api:latest $ECR_URI:latest

# Login to ECR
aws ecr get-login-password | docker login --username AWS --password-stdin $ECR_URI

# Push
docker push $ECR_URI:latest
```

### 4. Force new deployment

```bash
aws ecs update-service \
  --cluster northstar-api-cluster \
  --service northstar-api-service \
  --force-new-deployment
```

## Configuration

### Update Polestar Credentials

```bash
aws secretsmanager update-secret \
  --secret-id northstar/polestar-credentials \
  --secret-string '{"email":"your@email.com","password":"yourpassword"}'

# Restart to pick up new credentials
aws ecs update-service \
  --cluster northstar-api-cluster \
  --service northstar-api-service \
  --force-new-deployment
```

### Get API URL

```bash
aws cloudformation describe-stacks \
  --stack-name NorthStarInfrastructureStack \
  --query "Stacks[0].Outputs[?OutputKey=='LoadBalancerDNS'].OutputValue" \
  --output text
```

## Testing

```bash
# Health check
curl http://YOUR-LB-DNS/health

# Login
curl -X POST http://YOUR-LB-DNS/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"your@email.com","password":"yourpassword"}'
```

## Cleanup

```bash
cdk destroy
```

## CDK Commands

- `dotnet build src` - Build the CDK app
- `cdk synth` - Synthesize CloudFormation template
- `cdk diff` - Compare deployed stack with current state
- `cdk deploy` - Deploy stack
- `cdk destroy` - Delete all resources

## Home Assistant Setup

Configure your Home Assistant integration with the Load Balancer DNS from the stack outputs.

## Stack Outputs

- **LoadBalancerDNS**: API endpoint URL
- **RepositoryUri**: ECR repository for Docker images
- **SecretArn**: Secrets Manager ARN for credentials

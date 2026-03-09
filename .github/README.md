# GitHub Actions CI/CD

Automated deployment pipeline that builds Docker images, pushes to AWS ECR, and deploys to ECS Fargate.

## What It Does

On every push to `master` branch:
1. ✅ Builds Docker image
2. ✅ Pushes to AWS ECR
3. ✅ Deploys to ECS Fargate
4. ✅ Waits for deployment to stabilize
5. ✅ Reports API URL

## Setup

### 1. Deploy Infrastructure First

```bash
cd infrastructure
./deploy.sh
```

This creates the ECR repository and ECS cluster that the pipeline uses.

### 2. Configure GitHub OIDC

Run the setup script:

```bash
./.github/setup-github-oidc.sh
```

This will:
- Create GitHub OIDC provider in AWS (if needed)
- Create IAM role with permissions for ECR and ECS
- Output the Role ARN

### 3. Add GitHub Secret

1. Go to your repository settings: `Settings → Secrets and variables → Actions`
2. Click "New repository secret"
3. Name: `AWS_ROLE_ARN`
4. Value: The ARN from the setup script (e.g., `arn:aws:iam::123456789012:role/GitHubActionsNorthStarDeploy`)

## Manual Trigger

You can manually trigger the deployment:

1. Go to `Actions` tab
2. Select "Deploy to AWS Fargate"
3. Click "Run workflow"

## Permissions

The GitHub Actions workflow uses:
- **OIDC** (OpenID Connect) - No long-lived AWS credentials stored in GitHub
- **Least privilege** - Only permissions for ECR push and ECS deploy

The IAM role can:
- Push images to `northstar-api` ECR repository
- Update the `northstar-api-service` ECS service
- Describe CloudFormation stack for output retrieval

## Environment Variables

Edit these in `.github/workflows/docker-publish.yml` if needed:

```yaml
env:
  AWS_REGION: us-east-1                    # Your AWS region
  ECR_REPOSITORY: northstar-api            # ECR repo name (from CDK)
  ECS_CLUSTER: northstar-api-cluster       # ECS cluster name (from CDK)
  ECS_SERVICE: northstar-api-service       # ECS service name (from CDK)
```

## Monitoring

Watch deployments at: `https://github.com/YOUR_USERNAME/NorthStar.Api/actions`

Each deployment shows:
- Docker build logs
- ECR push status
- ECS deployment progress
- Final API URL

## Troubleshooting

### "Role cannot be assumed"

1. Verify `AWS_ROLE_ARN` secret is correct
2. Check trust policy allows your repository:
   ```bash
   aws iam get-role --role-name GitHubActionsNorthStarDeploy
   ```

### "Unable to locate credentials"

The OIDC setup is not complete. Re-run `.github/setup-github-oidc.sh`

### "Service does not exist"

Deploy infrastructure first:
```bash
cd infrastructure && ./deploy.sh
```

## Security

✅ **No AWS keys in GitHub** - Uses temporary OIDC tokens  
✅ **Scoped permissions** - Role can only deploy NorthStar API  
✅ **Audit trail** - All deployments logged in CloudWatch  

## Local Development

For local deployments (doesn't use GitHub Actions):

```bash
./deploy-to-aws.sh
```

This uses your local AWS CLI credentials.

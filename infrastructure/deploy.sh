#!/bin/bash
set -e

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
cd "$SCRIPT_DIR"

AWS_REGION="${AWS_REGION:-eu-west-1}"
REPO_STACK="NorthStarRepositoryStack"
SERVICE_STACK="NorthStarServiceStack"

echo "========================================="
echo "NorthStar Infrastructure Deployment"
echo "========================================="
echo "Region: $AWS_REGION"
echo ""

# Step 1: Deploy Repository Stack (ECR only)
echo "Step 1/3: Deploying ECR Repository stack..."
npx cdk deploy $REPO_STACK --require-approval never

ECR_URI=$(aws cloudformation describe-stacks \
    --stack-name $REPO_STACK \
    --query "Stacks[0].Outputs[?OutputKey=='RepositoryUri'].OutputValue" \
    --output text \
    --region $AWS_REGION)

echo "ECR Repository: $ECR_URI"
echo ""

# Step 2: Build and push Docker image to ECR
echo "Step 2/3: Building and pushing Docker image..."
cd "$SCRIPT_DIR/.."
docker build -t northstar-api:latest .
docker tag northstar-api:latest $ECR_URI:latest
aws ecr get-login-password --region $AWS_REGION | docker login --username AWS --password-stdin $ECR_URI
docker push $ECR_URI:latest
cd "$SCRIPT_DIR"

echo "Image pushed successfully"
echo ""

# Step 3: Deploy Service Stack (ECS Fargate + ALB)
echo "Step 3/3: Deploying ECS Service stack..."
npx cdk deploy $SERVICE_STACK --require-approval never

LB_DNS=$(aws cloudformation describe-stacks \
    --stack-name $SERVICE_STACK \
    --query "Stacks[0].Outputs[?OutputKey=='LoadBalancerDNS'].OutputValue" \
    --output text \
    --region $AWS_REGION)

echo ""
echo "========================================="
echo "Deployment complete!"
echo "========================================="
echo "API URL: http://$LB_DNS"
echo ""
echo "Wait 2-3 minutes for the service to start, then test:"
echo "  curl http://$LB_DNS/health"

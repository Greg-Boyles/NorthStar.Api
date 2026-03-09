#!/bin/bash
set -e

echo "🔄 Completing NorthStar Deployment"
echo "==================================="

AWS_REGION="eu-west-1"

# Wait for stack to complete
echo "Waiting for CloudFormation stack to complete..."
aws cloudformation wait stack-create-complete \
    --stack-name NorthStarInfrastructureStack \
    --region $AWS_REGION

echo "✅ Stack created successfully!"

# Get ECR URI
ECR_URI=$(aws cloudformation describe-stacks \
    --stack-name NorthStarInfrastructureStack \
    --query "Stacks[0].Outputs[?OutputKey=='RepositoryUri'].OutputValue" \
    --output text \
    --region $AWS_REGION)

echo ""
echo "📦 ECR Repository: $ECR_URI"

# Build Docker image
echo ""
echo "🐳 Building Docker image..."
cd ..
docker build -t northstar-api:latest .

# Tag for ECR
echo "🏷️  Tagging image..."
docker tag northstar-api:latest $ECR_URI:latest

# Login to ECR
echo "🔐 Logging in to ECR..."
aws ecr get-login-password --region $AWS_REGION | docker login --username AWS --password-stdin $ECR_URI

# Push to ECR
echo "⬆️  Pushing to ECR..."
docker push $ECR_URI:latest

# Update ECS service
echo "🚀 Updating ECS service..."
aws ecs update-service \
    --cluster northstar-api-cluster \
    --service northstar-api-service \
    --force-new-deployment \
    --region $AWS_REGION

# Get load balancer DNS
LB_DNS=$(aws cloudformation describe-stacks \
    --stack-name NorthStarInfrastructureStack \
    --query "Stacks[0].Outputs[?OutputKey=='LoadBalancerDNS'].OutputValue" \
    --output text \
    --region $AWS_REGION)

echo ""
echo "✅ Deployment complete!"
echo "======================================"
echo "🌐 API URL: http://$LB_DNS"
echo ""
echo "⏳ Wait 2-3 minutes for the service to start, then test:"
echo "   curl http://$LB_DNS/health"

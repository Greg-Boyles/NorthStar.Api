#!/bin/bash
set -e

echo "🔐 GitHub OIDC Setup for AWS"
echo "=============================="
echo ""

# Get GitHub repo info
REPO_OWNER=$(git config --get remote.origin.url | sed -n 's/.*github.com[:/]\([^/]*\)\/.*/\1/p')
REPO_NAME=$(git config --get remote.origin.url | sed -n 's/.*github.com[:/][^/]*\/\(.*\)\.git/\1/p')

if [ -z "$REPO_NAME" ]; then
    REPO_NAME=$(git config --get remote.origin.url | sed -n 's/.*github.com[:/][^/]*\/\(.*\)/\1/p')
fi

echo "📦 GitHub Repository: $REPO_OWNER/$REPO_NAME"
echo ""

# Check if OIDC provider exists
echo "1️⃣ Checking for GitHub OIDC provider..."
OIDC_PROVIDER=$(aws iam list-open-id-connect-providers --query "OpenIDConnectProviderList[?contains(Arn, 'token.actions.githubusercontent.com')].Arn" --output text 2>/dev/null || echo "")

if [ -z "$OIDC_PROVIDER" ]; then
    echo "   Creating OIDC provider..."
    aws iam create-open-id-connect-provider \
        --url https://token.actions.githubusercontent.com \
        --client-id-list sts.amazonaws.com \
        --thumbprint-list 6938fd4d98bab03faadb97b34396831e3780aea1
    
    OIDC_PROVIDER=$(aws iam list-open-id-connect-providers --query "OpenIDConnectProviderList[?contains(Arn, 'token.actions.githubusercontent.com')].Arn" --output text)
    echo "   ✅ Created: $OIDC_PROVIDER"
else
    echo "   ✅ Already exists: $OIDC_PROVIDER"
fi

# Get AWS account ID
AWS_ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
echo ""
echo "2️⃣ Creating IAM role for GitHub Actions..."

# Create trust policy
cat > /tmp/trust-policy.json <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": {
        "Federated": "$OIDC_PROVIDER"
      },
      "Action": "sts:AssumeRoleWithWebIdentity",
      "Condition": {
        "StringEquals": {
          "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
        },
        "StringLike": {
          "token.actions.githubusercontent.com:sub": "repo:$REPO_OWNER/$REPO_NAME:*"
        }
      }
    }
  ]
}
EOF

# Create role
ROLE_NAME="GitHubActionsNorthStarDeploy"
aws iam create-role \
    --role-name $ROLE_NAME \
    --assume-role-policy-document file:///tmp/trust-policy.json \
    --description "Role for GitHub Actions to deploy NorthStar API" \
    2>/dev/null || echo "   ⚠️  Role already exists, updating trust policy..."

aws iam update-assume-role-policy \
    --role-name $ROLE_NAME \
    --policy-document file:///tmp/trust-policy.json \
    2>/dev/null

echo "   ✅ Role created: $ROLE_NAME"

# Create and attach policy
echo ""
echo "3️⃣ Attaching permissions..."

cat > /tmp/permissions-policy.json <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "ecr:GetAuthorizationToken",
        "ecr:BatchCheckLayerAvailability",
        "ecr:GetDownloadUrlForLayer",
        "ecr:BatchGetImage",
        "ecr:PutImage",
        "ecr:InitiateLayerUpload",
        "ecr:UploadLayerPart",
        "ecr:CompleteLayerUpload"
      ],
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "ecs:UpdateService",
        "ecs:DescribeServices"
      ],
      "Resource": "arn:aws:ecs:*:$AWS_ACCOUNT:service/northstar-api-cluster/northstar-api-service"
    },
    {
      "Effect": "Allow",
        "Action": [
        "cloudformation:DescribeStacks"
      ],
      "Resource": "arn:aws:cloudformation:*:$AWS_ACCOUNT:stack/NorthStarInfrastructureStack/*"
    }
  ]
}
EOF

aws iam put-role-policy \
    --role-name $ROLE_NAME \
    --policy-name GitHubActionsDeployPolicy \
    --policy-document file:///tmp/permissions-policy.json

echo "   ✅ Permissions attached"

# Get role ARN
ROLE_ARN="arn:aws:iam::$AWS_ACCOUNT:role/$ROLE_NAME"

echo ""
echo "✅ Setup complete!"
echo "=============================="
echo ""
echo "📝 Next steps:"
echo ""
echo "1. Add this secret to your GitHub repository:"
echo "   Go to: https://github.com/$REPO_OWNER/$REPO_NAME/settings/secrets/actions"
echo ""
echo "   Secret name: AWS_ROLE_ARN"
echo "   Secret value: $ROLE_ARN"
echo ""
echo "2. Push your code to trigger the deployment:"
echo "   git add .github/workflows/docker-publish.yml"
echo "   git commit -m 'Update CI/CD to deploy to AWS Fargate'"
echo "   git push"
echo ""
echo "3. Watch the deployment:"
echo "   https://github.com/$REPO_OWNER/$REPO_NAME/actions"
echo ""

# Cleanup
rm /tmp/trust-policy.json /tmp/permissions-policy.json

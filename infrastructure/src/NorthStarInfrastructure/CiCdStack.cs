using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace NorthStarInfrastructure
{
    public class CiCdStack : Stack
    {
        public IRole GitHubActionsRole { get; }

        internal CiCdStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // GitHub OIDC Provider
            var oidcProvider = new OpenIdConnectProvider(this, "GitHubOidc", new OpenIdConnectProviderProps
            {
                Url = "https://token.actions.githubusercontent.com",
                ClientIds = new[] { "sts.amazonaws.com" }
            });

            // IAM Role for GitHub Actions - scoped to the NorthStar.Api repo
            GitHubActionsRole = new Role(this, "GitHubActionsRole", new RoleProps
            {
                RoleName = "GitHubActionsNorthStarDeploy",
                Description = "GitHub Actions role for NorthStar API deployment",
                AssumedBy = new FederatedPrincipal(
                    oidcProvider.OpenIdConnectProviderArn,
                    new Dictionary<string, object>
                    {
                        {
                            "StringEquals", new Dictionary<string, string>
                            {
                                { "token.actions.githubusercontent.com:aud", "sts.amazonaws.com" }
                            }
                        },
                        {
                            "StringLike", new Dictionary<string, string>
                            {
                                { "token.actions.githubusercontent.com:sub", "repo:Greg-Boyles/NorthStar.Api:*" }
                            }
                        }
                    },
                    "sts:AssumeRoleWithWebIdentity"
                ),
                InlinePolicies = new Dictionary<string, PolicyDocument>
                {
                    {
                        "NorthStarDeployPolicy", new PolicyDocument(new PolicyDocumentProps
                        {
                            Statements = new[]
                            {
                                // ECR auth
                                new PolicyStatement(new PolicyStatementProps
                                {
                                    Sid = "ECRAuth",
                                    Actions = new[] { "ecr:GetAuthorizationToken" },
                                    Resources = new[] { "*" }
                                }),
                                // ECR push
                                new PolicyStatement(new PolicyStatementProps
                                {
                                    Sid = "ECRPush",
                                    Actions = new[]
                                    {
                                        "ecr:BatchCheckLayerAvailability",
                                        "ecr:GetDownloadUrlForLayer",
                                        "ecr:BatchGetImage",
                                        "ecr:PutImage",
                                        "ecr:InitiateLayerUpload",
                                        "ecr:UploadLayerPart",
                                        "ecr:CompleteLayerUpload"
                                    },
                                    Resources = new[] { $"arn:aws:ecr:{Region}:{Account}:repository/northstar-api" }
                                }),
                                // ECS deploy
                                new PolicyStatement(new PolicyStatementProps
                                {
                                    Sid = "ECSDeploy",
                                    Actions = new[]
                                    {
                                        "ecs:UpdateService",
                                        "ecs:DescribeServices"
                                    },
                                    Resources = new[] { "*" },
                                    Conditions = new Dictionary<string, object>
                                    {
                                        {
                                            "ArnEquals", new Dictionary<string, string>
                                            {
                                                { "ecs:cluster", $"arn:aws:ecs:{Region}:{Account}:cluster/northstar-api-cluster" }
                                            }
                                        }
                                    }
                                }),
                                // CloudFormation read for deployment status
                                new PolicyStatement(new PolicyStatementProps
                                {
                                    Sid = "CloudFormationRead",
                                    Actions = new[] { "cloudformation:DescribeStacks" },
                                    Resources = new[] { $"arn:aws:cloudformation:{Region}:{Account}:stack/NorthStarServiceStack/*" }
                                })
                            }
                        })
                    }
                }
            });

            _ = new CfnOutput(this, "GitHubActionsRoleArn", new CfnOutputProps
            {
                Value = GitHubActionsRole.RoleArn,
                Description = "IAM Role ARN for GitHub Actions - add as AWS_ROLE_ARN secret in GitHub",
                ExportName = "NorthStarGitHubActionsRoleArn"
            });
        }
    }
}

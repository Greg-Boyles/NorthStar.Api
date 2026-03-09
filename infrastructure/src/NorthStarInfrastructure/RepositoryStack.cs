using Amazon.CDK;
using Amazon.CDK.AWS.ECR;
using Constructs;

namespace NorthStarInfrastructure
{
    public class RepositoryStack : Stack
    {
        public IRepository Repository { get; }

        internal RepositoryStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            var repo = new Repository(this, "NorthStarRepository", new RepositoryProps
            {
                RepositoryName = "northstar-api",
                RemovalPolicy = RemovalPolicy.DESTROY,
                EmptyOnDelete = true,
                LifecycleRules = new[]
                {
                    // Keep only the 2 most recent version-tagged images
                    new LifecycleRule
                    {
                        RulePriority = 1,
                        Description = "Keep last 2 version-tagged images",
                        TagPrefixList = new[] { "1.", "2.", "3." },
                        MaxImageCount = 2
                    },
                    // Remove any untagged images
                    new LifecycleRule
                    {
                        RulePriority = 2,
                        Description = "Remove untagged images",
                        TagStatus = TagStatus.UNTAGGED,
                        MaxImageAge = Duration.Days(1)
                    }
                }
            });

            Repository = repo;

            _ = new CfnOutput(this, "RepositoryUri", new CfnOutputProps
            {
                Value = Repository.RepositoryUri,
                Description = "ECR Repository URI for pushing Docker images",
                ExportName = "NorthStarEcrUri"
            });
        }
    }
}

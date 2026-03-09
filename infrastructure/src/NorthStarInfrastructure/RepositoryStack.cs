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
            Repository = new Repository(this, "NorthStarRepository", new RepositoryProps
            {
                RepositoryName = "northstar-api",
                RemovalPolicy = RemovalPolicy.DESTROY,
                EmptyOnDelete = true
            });

            _ = new CfnOutput(this, "RepositoryUri", new CfnOutputProps
            {
                Value = Repository.RepositoryUri,
                Description = "ECR Repository URI for pushing Docker images",
                ExportName = "NorthStarEcrUri"
            });
        }
    }
}

using Amazon.CDK;

namespace NorthStarInfrastructure
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var app = new App();

            Tags.Of(app).Add("project", "NorthStar.Api");

            var env = new Amazon.CDK.Environment
            {
                Account = "490204853569",
                Region = "eu-west-1",
            };

            // Stack 1: ECR Repository (deploy first, push image, then deploy remaining stacks)
            var repoStack = new RepositoryStack(app, "NorthStarRepositoryStack", new StackProps
            {
                Env = env
            });

            // Stack 2: ElastiCache Redis
            var redisStack = new RedisStack(app, "NorthStarRedisStack", new StackProps
            {
                Env = env
            });

            // Stack 3: ECS Fargate Service (requires image in ECR and Redis endpoint)
            var serviceStack = new ServiceStack(app, "NorthStarServiceStack", new ServiceStackProps
            {
                Env = env,
                Repository = repoStack.Repository,
                RedisEndpoint = redisStack.RedisEndpoint
            });

            serviceStack.AddDependency(repoStack);
            serviceStack.AddDependency(redisStack);

            // Stack 4: CI/CD (GitHub OIDC + IAM role)
            new CiCdStack(app, "NorthStarCiCdStack", new StackProps
            {
                Env = env
            });

            app.Synth();
        }
    }
}

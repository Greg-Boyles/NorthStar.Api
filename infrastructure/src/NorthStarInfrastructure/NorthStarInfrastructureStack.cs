using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.ApplicationAutoScaling;
using Constructs;
using ElbHealthCheck = Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck;

namespace NorthStarInfrastructure
{
    public class NorthStarInfrastructureStack : Stack
    {
        internal NorthStarInfrastructureStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // VPC - Use default VPC to stay within free tier
            var vpc = Vpc.FromLookup(this, "DefaultVPC", new VpcLookupOptions
            {
                IsDefault = true
            });

            // ECS Cluster
            var cluster = new Cluster(this, "NorthStarCluster", new ClusterProps
            {
                Vpc = vpc,
                ClusterName = "northstar-api-cluster"
            });

            // ECR Repository for the Docker image
            var repository = new Repository(this, "NorthStarRepository", new RepositoryProps
            {
                RepositoryName = "northstar-api",
                RemovalPolicy = RemovalPolicy.DESTROY, // For dev - change to RETAIN for production
                EmptyOnDelete = true // Cleanup images on stack deletion
            });

            // Fargate Service with Application Load Balancer
            var fargateService = new ApplicationLoadBalancedFargateService(this, "NorthStarService", new ApplicationLoadBalancedFargateServiceProps
            {
                Cluster = cluster,
                ServiceName = "northstar-api-service",
                DesiredCount = 1, // Single instance for free tier
                
                // Task Definition
                TaskImageOptions = new ApplicationLoadBalancedTaskImageOptions
                {
                    Image = ContainerImage.FromEcrRepository(repository, "latest"),
                    ContainerName = "northstar-api",
                    ContainerPort = 8080, // .NET API port
                    
                    // Environment variables
                    Environment = new Dictionary<string, string>
                    {
                        { "ASPNETCORE_ENVIRONMENT", "Production" },
                        { "ASPNETCORE_URLS", "http://+:8080" },
                    }
                },
                
                // CPU and Memory - Smallest Fargate config for free tier eligibility
                Cpu = 256, // 0.25 vCPU
                MemoryLimitMiB = 512, // 0.5 GB
                
                // Public facing
                PublicLoadBalancer = true,
                AssignPublicIp = true,
                
                // Health check
                HealthCheckGracePeriod = Duration.Seconds(60)
            });

            // Configure health check on target group
            fargateService.TargetGroup.ConfigureHealthCheck(new ElbHealthCheck
            {
                Path = "/health", // Add a health endpoint to your API
                Interval = Duration.Seconds(30),
                Timeout = Duration.Seconds(5),
                HealthyThresholdCount = 2,
                UnhealthyThresholdCount = 3
            });

            // Auto-scaling (optional - can scale to 0 when not in use to save costs)
            var scaling = fargateService.Service.AutoScaleTaskCount(new EnableScalingProps
            {
                MinCapacity = 1,
                MaxCapacity = 2
            });

            // Scale based on CPU utilization
            scaling.ScaleOnCpuUtilization("CpuScaling", new CpuUtilizationScalingProps
            {
                TargetUtilizationPercent = 70,
                ScaleInCooldown = Duration.Seconds(60),
                ScaleOutCooldown = Duration.Seconds(60)
            });

            // Outputs
            _ = new CfnOutput(this, "LoadBalancerDNS", new CfnOutputProps
            {
                Value = fargateService.LoadBalancer.LoadBalancerDnsName,
                Description = "DNS name of the load balancer",
                ExportName = "NorthStarApiUrl"
            });

            _ = new CfnOutput(this, "RepositoryUri", new CfnOutputProps
            {
                Value = repository.RepositoryUri,
                Description = "ECR Repository URI for pushing Docker images",
                ExportName = "NorthStarEcrUri"
            });

        }
    }
}

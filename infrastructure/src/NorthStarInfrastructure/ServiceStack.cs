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
    public class ServiceStackProps : StackProps
    {
        public IRepository Repository { get; set; }
    }

    public class ServiceStack : Stack
    {
        internal ServiceStack(Construct scope, string id, ServiceStackProps props) : base(scope, id, props)
        {
            var repository = props.Repository;
            var imageTag = (string)this.Node.TryGetContext("imageTag") ?? "latest";

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

            // Fargate Service with Application Load Balancer
            var fargateService = new ApplicationLoadBalancedFargateService(this, "NorthStarService", new ApplicationLoadBalancedFargateServiceProps
            {
                Cluster = cluster,
                ServiceName = "northstar-api-service",
                DesiredCount = 1,

                TaskImageOptions = new ApplicationLoadBalancedTaskImageOptions
                {
                    Image = ContainerImage.FromEcrRepository(repository, imageTag),
                    ContainerName = "northstar-api",
                    ContainerPort = 8080,

                    Environment = new Dictionary<string, string>
                    {
                        { "ASPNETCORE_ENVIRONMENT", "Production" },
                        { "ASPNETCORE_URLS", "http://+:8080" },
                    }
                },

                Cpu = 256,
                MemoryLimitMiB = 512,

                PublicLoadBalancer = true,
                AssignPublicIp = true,

                HealthCheckGracePeriod = Duration.Seconds(60)
            });

            // Health check
            fargateService.TargetGroup.ConfigureHealthCheck(new ElbHealthCheck
            {
                Path = "/health",
                Interval = Duration.Seconds(30),
                Timeout = Duration.Seconds(5),
                HealthyThresholdCount = 2,
                UnhealthyThresholdCount = 3
            });

            // Auto-scaling
            var scaling = fargateService.Service.AutoScaleTaskCount(new EnableScalingProps
            {
                MinCapacity = 1,
                MaxCapacity = 2
            });

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

        }
    }
}

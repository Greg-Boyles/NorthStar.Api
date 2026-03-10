using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ElastiCache;
using Constructs;

namespace NorthStarInfrastructure
{
    public class RedisStack : Stack
    {
        public string RedisEndpoint { get; private set; }

        internal RedisStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // Use default VPC
            var vpc = Vpc.FromLookup(this, "DefaultVPC", new VpcLookupOptions
            {
                IsDefault = true
            });

            // Security group for Redis - allows inbound from VPC CIDR
            var redisSecurityGroup = new SecurityGroup(this, "RedisSecurityGroup", new SecurityGroupProps
            {
                Vpc = vpc,
                Description = "Security group for ElastiCache Redis",
                AllowAllOutbound = false
            });

            redisSecurityGroup.AddIngressRule(
                Peer.Ipv4(vpc.VpcCidrBlock),
                Port.Tcp(6379),
                "Allow Redis access from VPC"
            );

            // Subnet group - use all available subnets
            var subnetGroup = new CfnSubnetGroup(this, "RedisSubnetGroup", new CfnSubnetGroupProps
            {
                Description = "Subnet group for ElastiCache Redis",
                SubnetIds = vpc.SelectSubnets(new SubnetSelection { SubnetType = SubnetType.PUBLIC }).SubnetIds,
                CacheSubnetGroupName = "northstar-redis-subnet-group"
            });

            // ElastiCache Redis cluster - single node t4g.micro for free tier
            var redisCluster = new CfnCacheCluster(this, "RedisCluster", new CfnCacheClusterProps
            {
                CacheNodeType = "cache.t4g.micro",
                Engine = "redis",
                NumCacheNodes = 1,
                ClusterName = "northstar-redis",
                CacheSubnetGroupName = subnetGroup.CacheSubnetGroupName,
                VpcSecurityGroupIds = new[] { redisSecurityGroup.SecurityGroupId },
                Port = 6379
            });

            redisCluster.AddDependency(subnetGroup);

            // Export Redis endpoint for ServiceStack to import
            RedisEndpoint = redisCluster.AttrRedisEndpointAddress;

            _ = new CfnOutput(this, "RedisEndpointOutput", new CfnOutputProps
            {
                Value = RedisEndpoint,
                Description = "ElastiCache Redis endpoint",
                ExportName = "NorthStarRedisEndpoint"
            });

            _ = new CfnOutput(this, "RedisPort", new CfnOutputProps
            {
                Value = "6379",
                Description = "Redis port",
                ExportName = "NorthStarRedisPort"
            });
        }
    }
}

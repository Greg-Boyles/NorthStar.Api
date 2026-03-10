# NorthStar.Api Helm Chart

A Helm chart for deploying NorthStar.Api to Kubernetes. This chart provides a production-ready deployment with optional ingress, autoscaling, and Redis integration for streaming support.

## Prerequisites

- Kubernetes 1.19+
- Helm 3.0+
- Redis instance (for streaming support)

## Installation

### Quick Start

1. **Create a namespace:**
   ```bash
   kubectl create namespace northstar
   ```

2. **Install the chart:**
   ```bash
   helm install northstar ./charts/northstar-api \
     --namespace northstar
   ```

**Note:** No secrets needed! Polestar credentials are submitted by clients (e.g., Home Assistant) when they call `POST /api/auth/login`. The API is stateless.

### Custom Values

Create a `my-values.yaml` file (**keep this private!**):

```yaml
replicaCount: 2

image:
  repository: ghcr.io/greg-boyles/northstar-api
  tag: "1.0.0"

ingress:
  enabled: true
  className: nginx
  hosts:
    - host: northstar.example.com
      paths:
        - path: /
          pathType: Prefix
  tls:
    - secretName: northstar-tls
      hosts:
        - northstar.example.com

env:
  REDIS_ENDPOINT: "redis-master.redis.svc.cluster.local:6379"
  ASPNETCORE_ENVIRONMENT: "Production"

resources:
  limits:
    cpu: 1000m
    memory: 1Gi
  requests:
    cpu: 500m
    memory: 512Mi

autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 5
  targetCPUUtilizationPercentage: 70
```

Install with custom values:
```bash
helm install northstar ./charts/northstar-api \
  --namespace northstar \
  --values my-values.yaml
```

## Configuration

### Required Configuration

| Parameter | Description | Example |
|-----------|-------------|---------|
| `env.REDIS_ENDPOINT` | Redis connection string | `redis:6379` |

### Common Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| `replicaCount` | Number of replicas | `1` |
| `image.repository` | Container image repository | `ghcr.io/greg-boyles/northstar-api` |
| `image.tag` | Container image tag | `""` (uses appVersion) |
| `service.type` | Kubernetes service type | `ClusterIP` |
| `service.port` | Service port | `80` |
| `ingress.enabled` | Enable ingress | `false` |
| `resources.limits.cpu` | CPU limit | `500m` |
| `resources.limits.memory` | Memory limit | `512Mi` |

See `values.yaml` for all available options.

## Authentication

The API **does not store or require** Polestar credentials. Clients (like Home Assistant) submit credentials when authenticating:

```bash
curl -X POST http://northstar-api/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"user@example.com","password":"password"}'
```

The API returns access and refresh tokens. The API is completely stateless - no credentials are stored.

## Redis Deployment

NorthStar.Api requires Redis for streaming support. You can deploy Redis using Bitnami's chart:

```bash
helm repo add bitnami https://charts.bitnami.com/bitnami
helm install redis bitnami/redis \
  --namespace redis \
  --create-namespace \
  --set auth.enabled=false \
  --set architecture=standalone
```

Then update your values:
```yaml
env:
  REDIS_ENDPOINT: "redis-master.redis.svc.cluster.local:6379"
```

## Upgrading

```bash
helm upgrade northstar ./charts/northstar-api \
  --namespace northstar \
  --values my-values.yaml
```

## Uninstalling

```bash
helm uninstall northstar --namespace northstar
```

## Troubleshooting

### Check pod status:
```bash
kubectl get pods -n northstar
```

### View logs:
```bash
kubectl logs -n northstar -l app.kubernetes.io/name=northstar-api
```

### Check health endpoint:
```bash
kubectl port-forward -n northstar svc/northstar 8080:80
curl http://localhost:8080/health
```

### Common Issues

1. **Pods CrashLoopBackOff**: Check logs for missing secrets or Redis connection issues
2. **ImagePullBackOff**: Ensure image repository and tag are correct, and imagePullSecrets are configured if needed
3. **Ingress not working**: Verify ingress controller is installed and className matches

## Examples

### Minimal Development Setup
```yaml
# dev-values.yaml
replicaCount: 1
env:
  ASPNETCORE_ENVIRONMENT: "Development"
  REDIS_ENDPOINT: "redis:6379"
```

### Production Setup with HA
```yaml
# prod-values.yaml
replicaCount: 3
autoscaling:
  enabled: true
  minReplicas: 3
  maxReplicas: 10
ingress:
  enabled: true
  className: nginx
  annotations:
    cert-manager.io/cluster-issuer: "letsencrypt-prod"
  hosts:
    - host: northstar.example.com
      paths:
        - path: /
          pathType: Prefix
resources:
  limits:
    cpu: 1000m
    memory: 1Gi
  requests:
    cpu: 500m
    memory: 512Mi
affinity:
  podAntiAffinity:
    preferredDuringSchedulingIgnoredDuringExecution:
      - weight: 100
        podAffinityTerm:
          labelSelector:
            matchExpressions:
              - key: app.kubernetes.io/name
                operator: In
                values:
                  - northstar-api
          topologyKey: kubernetes.io/hostname
```

## Security Best Practices

1. **Never commit real credentials** to Git - use a separate private repo for production values
2. **Use External Secrets Operator** for production deployments
3. **Enable network policies** to restrict pod-to-pod communication
4. **Use Pod Security Standards** (configured in podSecurityContext/securityContext)
5. **Regularly update** the image to get security patches

## Support

- GitHub Issues: https://github.com/Greg-Boyles/NorthStar.Api/issues
- Documentation: https://github.com/Greg-Boyles/NorthStar.Api

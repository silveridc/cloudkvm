# KvmControl

## Runtime requirements

- Control requires `ConnectionStrings:Redis` outside the `Development` environment.
- Set `ManagementApi:Token` using an environment variable or a secret store.
- Set each `Clusters:Nodes:{node}:Rpc:Token` using an environment variable or a secret store.
- Cluster requires Linux `libvirt`, `qemu-img`, `genisoimage`, `websockify`, `ip`, `nft`, and optionally `ovs-vsctl`.
- Cluster network changes are deny-by-default. Configure `Network:AllowedBridgePrefixes`, `Network:AllowedPorts`, and the NAT CIDR allowlists before use.
- VM creation is deny-by-default for bridges until `Provisioning:AllowedBridges` is configured.

## Development secrets

Do not commit development tokens. Set them for a local shell, for example:

```text
Control:
  ManagementApi__Token=<local-secret>
  Clusters__Nodes__local__Rpc__Token=<local-secret>
  ConnectionStrings__Redis=localhost:6379,abortConnect=false

Cluster:
  Rpc__Token=<same-local-secret>
```

- Use HTTPS for the Control-to-Cluster address. Cleartext `http://` node addresses are rejected.
- For a self-signed Cluster certificate, set `Clusters:Nodes:{node}:AllowSelfSignedCertificate=true`; certificate hostname validation remains enabled.

## Regression checks

Run the executable checks (no additional test-framework packages):

```powershell
dotnet run --project Tests/Kvm.RegressionTests
```

The default run checks in-memory operation idempotency, payload conflicts, node
case normalization, and single-use VNC tickets. To also exercise the production
Redis Lua and GETDEL paths, use a disposable test Redis instance:

```powershell
$env:KVM_TEST_REDIS = 'localhost:6379,abortConnect=true'
try {
    dotnet run --project Tests/Kvm.RegressionTests
} finally {
    Remove-Item Env:KVM_TEST_REDIS -ErrorAction SilentlyContinue
}
```

Redis checks use two independent service instances sharing a unique per-run key
prefix. They delete only their own operation keys; they never flush the database.
These are executable regression checks, not a `dotnet test` test project. A failed
assertion exits nonzero. Without `KVM_TEST_REDIS`, Redis checks explicitly print
`SKIP`; memory-only success does not verify Redis or Linux/KVM integration.

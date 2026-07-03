# Optional Integration Guidance

SharedMemoryStore keeps the core package focused on the concrete `MemoryStore`
API. The package does not add hosting, dependency injection, logging,
health-check, or options-framework dependencies.

Service applications can wrap the concrete store in narrow application-owned
boundaries:

- lifecycle start and stop around `MemoryStore.TryCreateOrOpen` and `Dispose`;
- configuration validation through `SharedMemoryStoreOptions.Validate`;
- health checks through `TryGetDiagnostics`;
- graceful shutdown through lease release, reservation abort, explicit recovery,
  and store disposal;
- read-only or write-only application interfaces only when a real consumer needs
  a smaller boundary.

Avoid broad interfaces that mirror every `MemoryStore` method. Low-level lease,
reservation, span, shared-memory layout, and recovery details are not good
application mocking boundaries.

See [Hosted service integration sample](../samples/HostedServiceIntegration/README.md)
for a dependency-light lifecycle and health wrapper. Applications that use
`Microsoft.Extensions.Hosting` can adapt the same narrow wrapper inside their
own hosted service without adding those dependencies to the core package.

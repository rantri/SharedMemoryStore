# Data Model: Linux, Windows, and Docker Support

## SupportedEnvironment

Represents an environment where the package promises ordinary runtime and
development workflows.

**Fields**
- `EnvironmentKind`: Windows, Linux, or DockerContainerProfile.
- `OperatingSystemFamily`: Windows or Linux.
- `Architecture`: Runtime architecture observed during validation.
- `Capabilities`: Set of available `PlatformCapability` values.
- `ValidationStatus`: Supported, unsupported, restricted, or unknown.

**Validation Rules**
- Windows and Linux environments must provide same-host shared storage,
  synchronization, process-liveness, permission, and cleanup capabilities.
- Docker container profiles must provide the same capabilities across all
  participating containers.
- Restricted environments must produce documented outcomes instead of silent
  isolated stores.

## PlatformCapability

Represents a host or container capability required to satisfy the public store
contract.

**Fields**
- `SharedRegion`: Whether participants can access the same mapped bytes.
- `SharedSynchronization`: Whether participants can coordinate mutual exclusion.
- `OwnerLiveness`: Whether active owners can be classified as current, live,
  stale, unsupported, or unsafe.
- `PermissionBoundary`: Whether participants have compatible access rights.
- `Cleanup`: Whether stale or final resources can be detected and cleaned.
- `Capacity`: Whether the shared memory area can satisfy requested store sizes.

**Relationships**
- Required by `SupportedEnvironment`.
- Consumed by `StoreResourceIdentity`, `SharedSynchronizationResource`, and
  `OwnerLivenessRecord`.

## StoreResourceIdentity

Represents the deterministic relationship between the public store name and the
platform-visible resources used to back the store.

**Fields**
- `PublicName`: Consumer-provided store name.
- `SanitizedName`: Platform-safe resource fragment derived from `PublicName`.
- `RegionResourceName`: Platform-visible shared-memory region identity.
- `SynchronizationResourceName`: Platform-visible synchronization identity.
- `MetadataResourceName`: Optional resource identity for cleanup or ownership
  metadata.
- `Scope`: Host-local or supported same-host container profile.

**Validation Rules**
- Names must be deterministic across participants using the same `PublicName`.
- Sanitization must avoid path traversal, reserved names, and platform-specific
  invalid characters.
- Different public names must not collide after sanitization.
- Resource identity must not expose arbitrary filesystem paths through the
  public store name.

## SharedMemoryRegion

Represents the mapped bytes that contain the existing store header, index, lease
registry, slot metadata, descriptors, and payloads.

**Fields**
- `Identity`: Associated `StoreResourceIdentity`.
- `Capacity`: Total mapped byte length.
- `OpenMode`: CreateNew, OpenExisting, or CreateOrOpen.
- `Access`: Read/write access required by the package.
- `State`: Missing, creating, ready, incompatible, access denied, unsupported,
  mapping failed, or disposed.

**State Transitions**
- `Missing -> Creating -> Ready` for successful CreateNew.
- `Missing -> Ready` for successful CreateOrOpen.
- `Ready -> Disposed` when a handle closes.
- `Ready -> Incompatible` when header or layout validation fails.
- `Ready -> Unsupported` when required capabilities are absent.
- `Creating/Ready -> MappingFailed` when the runtime cannot create or map the
  resource.

## SharedSynchronizationResource

Represents cross-process mutual exclusion for store open, read, write, remove,
recovery, diagnostics, and disposal operations.

**Fields**
- `Identity`: Associated `StoreResourceIdentity`.
- `WaitPolicy`: Default, no-wait, bounded wait, infinite wait, or canceled.
- `AcquisitionResult`: Success, busy, canceled, abandoned, disposed, unsupported,
  or access denied.

**Validation Rules**
- Wait behavior must match public timeout and cancellation contracts.
- Abandoned-owner situations must be converted to documented outcomes.
- Synchronization failure must not expose raw platform exceptions to callers.

## OwnerLivenessRecord

Represents the owner information used to decide whether lease or reservation
recovery can safely reclaim storage.

**Fields**
- `OwnerProcessId`: Process identifier recorded in the shared layout.
- `OwnerKind`: Current process, other live process, stale process, unsupported,
  or unsafe.
- `EnvironmentKind`: Host or container profile where classification occurred.
- `ClassificationReason`: Current owner, process still live, process missing,
  owner cannot be evaluated, or inconsistent record.

**Validation Rules**
- Unknown owners must not be treated as stale unless liveness can be evaluated
  safely.
- Container deployments that hide owner liveness must report unsupported or
  unsafe categories.
- Current-process recovery may recover only current-process owners when enabled.

## ContainerParticipant

Represents one process running in a Docker container that participates in a
same-host shared store.

**Fields**
- `Role`: Creator, writer, reader, verifier, or recovery owner.
- `ContainerIdentity`: Container name or generated test identity.
- `StoreName`: Public store name used by the participant.
- `CapabilitiesObserved`: Shared-region, synchronization, liveness, permission,
  and capacity capabilities observed during validation.
- `ExitBehavior`: Normal exit, abrupt exit, restart, or timeout.

**Relationships**
- Uses `StoreResourceIdentity`.
- Exercises `RuntimeWorkflow`.
- Contributes to `CrossPlatformValidationMatrix`.

## RuntimeWorkflow

Represents a consumer-visible scenario that must behave consistently across
supported environments.

**Fields**
- `WorkflowName`: Create/open, publish/acquire/release, remove/reuse,
  reservation commit, reservation abort, segmented publish, diagnostics, lease
  recovery, reservation recovery, disposal, or package consumption.
- `ExpectedStatuses`: Public status outcomes for success and failure paths.
- `RequiredCapabilities`: Capabilities needed for the workflow.
- `ValidationEnvironments`: Environments where the workflow must pass.

## CrossPlatformValidationMatrix

Represents the coverage record for supported environments.

**Fields**
- `Environment`: Windows, Linux, or supported Docker profile.
- `Workflow`: Runtime or development workflow being validated.
- `Command`: Repository command or sample command used for validation.
- `ExpectedOutcome`: Pass, skip with documented reason, or fail.
- `Evidence`: Test result, script output, sample output, or release note entry.

**Validation Rules**
- Every public runtime workflow must have Linux and Windows coverage.
- Docker container coverage must include cross-container visibility, active
  lease protection, diagnostics, and recovery.
- Package-consumption validation must run from a clean consumer project.

## PlatformLimitation

Represents a documented environment condition that prevents support.

**Fields**
- `LimitationKind`: Unsupported OS, missing shared-memory capability, missing
  synchronization capability, hidden owner liveness, access denied, insufficient
  capacity, isolated container resources, or restricted host policy.
- `AffectedWorkflows`: Runtime workflows affected by the limitation.
- `PublicOutcome`: StoreOpenStatus or StoreStatus reported to consumers.
- `ConsumerAction`: How the user can correct configuration or recognize that the
  scenario is out of scope.

## CompatibilityNote

Represents release-facing compatibility information.

**Fields**
- `ChangeArea`: Public API, status taxonomy, layout, package metadata, docs,
  samples, validation scripts, or runtime behavior.
- `Impact`: Compatible, additive, behavior correction, or breaking.
- `RequiredDocs`: README, portability guide, changelog, release notes, XML docs,
  sample README, or contract docs.
- `Validation`: Contract test, package test, sample run, or manual review needed
  before release.

namespace SharedMemoryStore;

/// <summary>
/// Options for explicit stale reservation recovery.
/// </summary>
/// <param name="RecoverCurrentProcessReservations">
/// When true, current-process reservations may be recovered for tests and
/// controlled shutdown after the application has quiesced every writer.
/// Initializing reservations remain protected until their participant enters
/// the explicit closing or recovering handoff.
/// </param>
public readonly record struct ReservationRecoveryOptions(bool RecoverCurrentProcessReservations);

/// <summary>
/// Summary returned by explicit stale reservation recovery.
/// </summary>
/// <param name="ScannedReservationCount">The number of pending reservations inspected.</param>
/// <param name="RecoveredReservationCount">The number of stale reservations reclaimed.</param>
/// <param name="ActiveReservationCount">The number of reservations still owned by live producers.</param>
/// <param name="UnsupportedReservationCount">The number whose owner liveness could not be evaluated safely.</param>
/// <param name="FailedRecoveryCount">The number whose shared state prevented safe recovery.</param>
public readonly record struct ReservationRecoveryReport(
    int ScannedReservationCount,
    int RecoveredReservationCount,
    int ActiveReservationCount,
    int UnsupportedReservationCount,
    int FailedRecoveryCount);

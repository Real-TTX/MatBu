namespace MatBu.Models;

/// <summary>
/// Human-readable live status of a running <see cref="TransferJob"/>. Unlike the coarse
/// <see cref="TransferJob.State"/> (Running/Completed/Failed) this reflects the current pipeline
/// activity and is surfaced verbatim in the UI.
/// </summary>
public static class JobPhase
{
    public const string Queued = "In Warteschlange";
    public const string Preparing = "Vorbereitung";
    public const string ConsistencyPause = "Anwendung wird pausiert (Konsistenz)";
    public const string Reading = "Lesen & Komprimieren";
    public const string ReadPausedSlowTransfer = "Lesen pausiert – Übertragung langsam";
    public const string Transferring = "Überträgt";
    public const string WaitingForTarget = "Wartet auf Ziel";
    public const string Writing = "Schreibt Ziel";
    public const string Integrity = "Prüft Integrität";
    public const string Retention = "Retention";
    public const string Finalizing = "Abschluss";
    public const string Completed = "Fertig";
    public const string Failed = "Fehlgeschlagen";
    public const string Cancelling = "Wird abgebrochen";
    public const string Cancelled = "Abgebrochen";
}

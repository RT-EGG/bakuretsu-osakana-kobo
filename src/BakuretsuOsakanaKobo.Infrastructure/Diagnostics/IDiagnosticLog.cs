namespace BakuretsuOsakanaKobo.Infrastructure.Diagnostics;

public interface IDiagnosticLog : IDisposable
{
    DiagnosticWriteResult Write(DiagnosticEvent diagnosticEvent);
}

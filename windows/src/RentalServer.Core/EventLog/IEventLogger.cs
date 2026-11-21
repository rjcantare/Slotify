namespace RentalServer.Core.EventLog;

/// <summary>
/// Surface used by every service that wants to drop an audit row. Kept as
/// an interface so test fakes can record what was written without touching
/// SQLite, and so a future async writer / queue can be swapped in without
/// changing call sites.
/// </summary>
public interface IEventLogger
{
    Task<long> AppendAsync(EventLogAppendRequest request, CancellationToken cancellationToken = default);
}

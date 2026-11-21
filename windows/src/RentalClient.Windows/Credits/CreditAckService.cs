using RentalShared.Mqtt;

namespace RentalClient.Windows.Credits;

// Dedup + ack orchestrator for inbound credit messages.
//
// Per MQTT Protocol "seqNo / ACK Contract":
//   - Server maintains monotonic lastCreditSeqNo per stationId.
//   - Station persists lastProcessedCreditSeqNo. Same dedup rule.
//   - Station ACKs EVEN ON DUPLICATE (otherwise server retries forever).
//
// For v1 the lastProcessedSeqNo is in-memory only. If the client restarts
// while a credit is in flight (server hasn't yet seen our ack), we'd
// re-process and re-toast on the replay. The session retained still drives
// the authoritative remaining time so no double-credit on the user, just a
// duplicate UX nudge. Client-side SQLite for persistent dedup is a later
// step if that becomes a real-world issue.
public sealed class CreditAckService
{
    private readonly object _gate = new();
    private long _lastProcessedSeqNo;

    // Fires only on a strictly-new credit (after dedup). Subscribers should
    // marshal to the UI thread themselves if needed — this event may fire
    // from a background MQTT thread.
    public event EventHandler<CreditMessage>? NewCreditApplied;

    public long LastProcessedSeqNo
    {
        get
        {
            lock (_gate) return _lastProcessedSeqNo;
        }
    }

    // Process an inbound credit: always publish the ACK (even on duplicate);
    // raise NewCreditApplied only on strictly-new seqNo.
    public async Task ProcessAsync(
        CreditMessage message,
        Func<long, Task> publishAckAsync,
        CancellationToken cancellationToken = default)
    {
        // ACK first — keep the server's retry quiet even on dup.
        await publishAckAsync(message.SeqNo);
        cancellationToken.ThrowIfCancellationRequested();

        bool isNew;
        lock (_gate)
        {
            if (message.SeqNo <= _lastProcessedSeqNo)
            {
                isNew = false;
            }
            else
            {
                _lastProcessedSeqNo = message.SeqNo;
                isNew = true;
            }
        }

        if (isNew)
        {
            NewCreditApplied?.Invoke(this, message);
        }
    }
}

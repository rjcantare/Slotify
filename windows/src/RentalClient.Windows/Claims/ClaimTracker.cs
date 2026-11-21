using System.Diagnostics;
using RentalShared.Mqtt;

namespace RentalClient.Windows.Claims;

// Tracks the station's current claim state, driven by claim/result messages
// from the server. The tracker is a passive mirror in S16a — there's no
// StartPending yet because the Insert Coin button (which publishes claim/request)
// lands in S16b.
//
// Anchors granted-window elapsed time monotonically via Stopwatch.GetTimestamp(),
// same pattern as SessionTracker. The 30 s claim window is a client-side
// cosmetic timer — the server's own TTL is the authority on whether a claim
// is still valid; this tracker reflects what the server told us when granting
// and ticks down a local mirror for UI display.
public sealed class ClaimTracker
{
    private readonly object _gate = new();
    private ClaimStatus _status = ClaimStatus.Idle;
    private string? _requestId;
    private string? _claimId;
    private string? _acceptorId;
    private long _windowSec;
    private long _grantedAtTimestamp;
    private string? _denyReason;
    private string? _heldBy;
    private string? _banUntil;
    // S18b/S21b: running total accumulated during the current Granted window.
    // Driven by the host (MainWindow) calling AddRunningCoin on NewCreditApplied.
    private long _runningTotalSec;
    private int _runningTotalPesos;

    public event EventHandler<ClaimTrackerState>? StateChanged;

    public void StartPending(string requestId)
    {
        lock (_gate)
        {
            _status = ClaimStatus.Pending;
            _requestId = requestId;
            ResetGrantStateLocked();
            ResetDenyStateLocked();
        }
        Emit();
    }

    public void Apply(ClaimResultMessage result)
    {
        lock (_gate)
        {
            // Only a result for the request this client is actively tracking may
            // change UI state. Extra click bursts can produce denied responses
            // while the original granted claim is still valid; those must not
            // overwrite the granted window.
            if (_status is ClaimStatus.Idle or ClaimStatus.TimedOut ||
                !string.Equals(_requestId, result.RequestId, StringComparison.Ordinal))
            {
                return;
            }

            _requestId = result.RequestId;

            if (string.Equals(result.Result, "granted", StringComparison.Ordinal))
            {
                if (_status == ClaimStatus.Granted &&
                    string.Equals(_claimId, result.ClaimId, StringComparison.Ordinal))
                {
                    // Window-extension refresh from a coin drop — re-anchor
                    // the countdown without resetting running totals.
                    _windowSec = result.WindowSec ?? 60;
                    _grantedAtTimestamp = Stopwatch.GetTimestamp();
                    ResetDenyStateLocked();
                }
                else
                {
                    _status = ClaimStatus.Granted;
                    _claimId = result.ClaimId;
                    _acceptorId = result.AcceptorId;
                    _windowSec = result.WindowSec ?? 60;
                    _grantedAtTimestamp = Stopwatch.GetTimestamp();
                    _runningTotalSec = 0;
                    _runningTotalPesos = 0;
                    ResetDenyStateLocked();
                }
            }
            else if (string.Equals(result.Result, "denied", StringComparison.Ordinal))
            {
                _status = ClaimStatus.Denied;
                _denyReason = result.Reason;
                _heldBy = result.HeldBy;
                _banUntil = result.BanUntil;
                ResetGrantStateLocked();
            }
        }
        Emit();
    }

    public void MarkTimedOut()
    {
        lock (_gate)
        {
            if (_status != ClaimStatus.Granted) return;
            _status = ClaimStatus.TimedOut;
        }
        Emit();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _status = ClaimStatus.Idle;
            _requestId = null;
            ResetGrantStateLocked();
            ResetDenyStateLocked();
        }
        Emit();
    }

    public ClaimTrackerState GetCurrentState()
    {
        lock (_gate) return ComputeStateLocked();
    }

    // S18b: add a coin's credit to the running window total. Only effective while
    // Status == Granted (a credit arriving outside a granted window is treated as
    // unrelated UX noise — e.g. admin/promo credit, which we don't want stacking
    // here). Called by MainWindow from CreditAckService.NewCreditApplied.
    public void AddRunningCredit(int secondsToAdd)
    {
        AddRunningCoin(secondsToAdd, denomination: null);
    }

    public void AddRunningCoin(int secondsToAdd, int? denomination)
    {
        if (secondsToAdd < 0) return;

        lock (_gate)
        {
            if (_status != ClaimStatus.Granted) return;
            _runningTotalSec += secondsToAdd;
            if (denomination is > 0)
            {
                _runningTotalPesos += denomination.Value;
            }
        }
        Emit();
    }

    private ClaimTrackerState ComputeStateLocked()
    {
        long elapsedSec = 0;
        if (_status == ClaimStatus.Granted)
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - _grantedAtTimestamp;
            elapsedSec = (long)(elapsedTicks / (double)Stopwatch.Frequency);
        }

        return new ClaimTrackerState(
            Status: _status,
            RequestId: _requestId,
            ClaimId: _claimId,
            AcceptorId: _acceptorId,
            WindowSec: _windowSec,
            ElapsedSec: elapsedSec,
            DenyReason: _denyReason,
            HeldBy: _heldBy,
            RunningTotalSec: _runningTotalSec,
            RunningTotalPesos: _runningTotalPesos,
            BanUntil: _banUntil);
    }

    private void ResetGrantStateLocked()
    {
        _claimId = null;
        _acceptorId = null;
        _windowSec = 0;
        _grantedAtTimestamp = 0;
        _runningTotalSec = 0;
        _runningTotalPesos = 0;
    }

    private void ResetDenyStateLocked()
    {
        _denyReason = null;
        _heldBy = null;
        _banUntil = null;
    }

    private void Emit()
    {
        StateChanged?.Invoke(this, GetCurrentState());
    }
}

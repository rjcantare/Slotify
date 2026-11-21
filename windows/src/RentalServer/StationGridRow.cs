using System.ComponentModel;
using System.Runtime.CompilerServices;
using RentalServer.Core.Stations;

namespace RentalServer;

/// <summary>
/// View-model wrapper around <see cref="StationRecord"/> that adds a live
/// "Time Remaining" column and color-coded status to the WPF station grid.
/// </summary>
public sealed class StationGridRow : INotifyPropertyChanged
{
    public string StationId { get; }
    public string StationName => string.IsNullOrWhiteSpace(DisplayName) ? StationId : DisplayName!;
    public string DeviceType { get; }
    public bool IsOnline { get; }
    public string? LastSeenAt { get; }
    public string? AppVersion { get; }
    public string? DisplayName { get; }

    private string _timeRemainingText = "\u2014";
    private string _operatorStatusText = "OFFLINE";
    private string _acceptorStateText = "UNKNOWN";
    private bool _hasActiveClaim;
    private bool _isLocked;

    public string TimeRemainingText
    {
        get => _timeRemainingText;
        set { if (_timeRemainingText == value) return; _timeRemainingText = value; OnPropertyChanged(); }
    }

    public string OperatorStatusText
    {
        get => _operatorStatusText;
        set { if (_operatorStatusText == value) return; _operatorStatusText = value; OnPropertyChanged(); }
    }

    public string AcceptorStateText
    {
        get => _acceptorStateText;
        set { if (_acceptorStateText == value) return; _acceptorStateText = value; OnPropertyChanged(); }
    }

    public bool HasActiveClaim
    {
        get => _hasActiveClaim;
        set { if (_hasActiveClaim == value) return; _hasActiveClaim = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackgroundColor)); }
    }

    public bool IsLocked
    {
        get => _isLocked;
        set { if (_isLocked == value) return; _isLocked = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackgroundColor)); }
    }

    public string BackgroundColor
    {
        get
        {
            if (!IsOnline) return "#FFF1F5F9";
            if (HasActiveClaim) return "#FFFEF3C7";
            if (_isLocked) return "#FFFEE2E2";
            return "#FFDCFCE7";
        }
    }

    public StationGridRow(StationRecord record)
    {
        StationId = record.StationId;
        DeviceType = record.DeviceType;
        IsOnline = record.IsOnline;
        LastSeenAt = record.LastSeenAt;
        AppVersion = record.AppVersion;
        DisplayName = record.DisplayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

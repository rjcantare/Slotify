namespace RentalServer.Core.Mqtt;

public sealed record MqttConnectionStatus(
    bool IsConnected,
    string Message,
    string? Detail = null);

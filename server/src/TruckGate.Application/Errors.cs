namespace TruckGate.Application;

public enum ErrorKind { Validation, NotFound, Conflict }

/// <summary>Business error with a stable code the UI can translate (ar/fr).</summary>
public sealed class TruckGateException(ErrorKind kind, string code, string message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;

    public static TruckGateException Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static TruckGateException NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static TruckGateException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
}

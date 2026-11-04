using System;

namespace KnightLock.Core;

public enum VaultError
{
    Unknown = 0,
    NotAVault,
    UnsupportedVersion,
    Corrupt,
    Truncated,
    WrongPassword,
    Io,
    Cancelled,
    AccessDenied,
    AlreadyExists,
    PasswordTooWeak,
    InvalidPath
}

/// <summary>Typed exception so the UI can show a precise, friendly ContentDialog.</summary>
public sealed class VaultException : Exception
{
    public VaultError Error { get; }

    public VaultException(VaultError error, string message, Exception? inner = null) : base(message, inner)
        => Error = error;

    /// <summary>Friendly title for dialog presentation.</summary>
    public string Title => Error switch
    {
        VaultError.NotAVault => "Not a KnightLock vault",
        VaultError.UnsupportedVersion => "Unsupported vault version",
        VaultError.Corrupt => "Vault is damaged",
        VaultError.Truncated => "Vault is incomplete",
        VaultError.WrongPassword => "Incorrect master password",
        VaultError.Io => "Storage error",
        VaultError.Cancelled => "Cancelled",
        VaultError.AccessDenied => "Access denied",
        VaultError.AlreadyExists => "Already exists",
        VaultError.PasswordTooWeak => "Password too weak",
        VaultError.InvalidPath => "Invalid location",
        _ => "Something went wrong"
    };

    public static VaultException From(Exception ex, VaultError fallback = VaultError.Unknown)
    {
        if (ex is VaultException vex) return vex;
        if (ex is UnauthorizedAccessException ua)
            return new VaultException(VaultError.AccessDenied,
                "KnightLock was denied access to a file or folder. Try running as administrator or pick another location.", ua);
        if (ex is OperationCanceledException oc)
            return new VaultException(VaultError.Cancelled, "The operation was cancelled.", oc);
        if (ex is System.IO.IOException io)
            return new VaultException(VaultError.Io, io.Message, io);
        return new VaultException(fallback, ex.Message, ex);
    }
}

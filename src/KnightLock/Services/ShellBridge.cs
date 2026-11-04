using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace KnightLock.Services;

/// <summary>
/// Hands a container path from a freshly launched process to the instance that is already running.
///
/// KnightLock is single-instance on purpose (two windows must never race over one container), so a
/// double-click on a .vault while it is open would otherwise just refuse to start. Instead the
/// second process writes the path to a per-session named pipe and exits; the running window picks
/// it up and opens the unlock panel.
/// </summary>
public static class ShellBridge
{
    private const string PipeName = "KnightLock.OpenVault";

    private static Thread? _server;

    /// <summary>Raised on the server thread with the path requested by a second process.</summary>
    public static event Action<string>? VaultRequested;

    /// <summary>Starts listening. Safe to call once, after the UI exists.</summary>
    public static void StartServer()
    {
        if (_server is not null) return;

        _server = new Thread(ServerLoop)
        {
            IsBackground = true,
            Name = "KnightLock shell bridge"
        };
        _server.Start();
    }

    /// <summary>Sends a path to the running instance. Returns false when nobody is listening.</summary>
    public static bool TrySend(string vaultPath, int timeoutMs = 800)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(vaultPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ServerLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = reader.ReadLine();

                if (!string.IsNullOrWhiteSpace(line))
                    VaultRequested?.Invoke(line.Trim());
            }
            catch (Exception ex)
            {
                Log.Warn($"Shell bridge listener error: {ex.Message}");
                Thread.Sleep(250);
            }
        }
    }
}

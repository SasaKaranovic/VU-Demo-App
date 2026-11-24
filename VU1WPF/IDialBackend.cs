using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KR_VU1_Server
{
    /// <summary>
    /// What the app needs from "a VU1 server". Two implementations:
    ///  - <see cref="VU1_Server"/>: HTTP client for an external VU1 server (default)
    ///  - VU1WPF.BuiltInServer.BuiltInDialServer: talks to the VU1 hub directly over USB
    ///    (compiled only when EnableBuiltInServer=true, see VU1WPF.csproj)
    /// All values use the same units as the external server's REST API.
    /// </summary>
    internal interface IDialBackend : IDisposable
    {
        /// <summary>Short description for the status bar, e.g. "http://localhost:5340" or "Built-in server · COM5".</summary>
        string ServerUrl { get; }

        /// <summary>True for the built-in (USB) server.</summary>
        bool IsBuiltIn { get; }

        bool IsOnline { get; }

        /// <summary>Why the server can't be used right now (shown to the user), or null.</summary>
        string? LastError { get; }
        int ConsecutiveFailures { get; }

        /// <summary>Raised (on any thread) when connectivity changes.</summary>
        event Action<bool>? ConnectionStateChanged;

        Task<bool> RefreshDialListAsync(CancellationToken ct = default);
        List<DialInfo> GetDialList();

        Task<bool> UpdateDialNameAsync(string uid, string dialName, CancellationToken ct = default);
        Task<bool> UpdateDialValueAsync(string uid, int val, CancellationToken ct = default);

        /// <summary>See <see cref="VU1_Server.UpdateDialBacklightAsync"/> for the value scaling.</summary>
        Task<bool> UpdateDialBacklightAsync(string uid, int red, int green, int blue, bool values_as_percent = false, CancellationToken ct = default);

        Task<bool> UploadDialImageAsync(string uid, byte[] imageBytes, string fileName = "dial.png", CancellationToken ct = default);

        /// <summary>Image currently on the dial (PNG), or null when unknown/blank.</summary>
        Task<byte[]?> GetDialImageAsync(string uid, CancellationToken ct = default);
    }
}

using Godot;

namespace Nebula.Diagnostics
{
    /// <summary>
    /// Whether this process is the synthetic load client.
    ///
    /// <para>Lives in Core, not beside the load client itself, because <c>Nebula.props</c> strips
    /// <c>Testing/**</c> from every non-Debug build — so a game autoload asking this question would
    /// stop compiling in a release build if the answer lived there. Same split as
    /// <see cref="Nebula.Bots.BotRunner"/>: the question is always answerable, the runtime that
    /// answers to it is not always present.</para>
    ///
    /// <para>Read from the command line rather than set by the load-client scene, because autoloads
    /// run before any scene does and it is autoloads that need the answer — a game's Steam or
    /// session bootstrap has no business running in a process that drives raw sockets.</para>
    /// </summary>
    public static class LoadClientProcess
    {
        /// <summary>Marks the process as the load client. Passed alongside the scene path.</summary>
        public const string LoadClientArg = "--loadClient";

        private static bool? _isLoadClient;

        /// <summary>True in a process launched with <see cref="LoadClientArg"/>.</summary>
        public static bool IsLoadClient
        {
            get
            {
                if (_isLoadClient.HasValue) return _isLoadClient.Value;
                foreach (var argument in OS.GetCmdlineArgs())
                {
                    if (argument != LoadClientArg) continue;
                    _isLoadClient = true;
                    return true;
                }
                _isLoadClient = false;
                return false;
            }
        }
    }
}

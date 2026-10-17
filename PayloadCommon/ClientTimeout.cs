// A payload that serves one client at a time must not be able to block on that client forever.
//
// The failure this prevents, observed on this console: a client went away without closing its socket, so
// the console's side stayed ESTABLISHED. The serve loop sat in recv, never returned, and never reached
// accept again. The listener kept completing TCP handshakes - so every port check said "open" - while
// nothing was ever answered. A payload cannot be unloaded, so that instance was dead for the rest of the
// session and the only way back was a fresh build on a new port.
//
// SO_RCVTIMEO turns that block into a timeout, and the serve loops already treat a failed read as
// end-of-client, so the instance recovers by itself.
//
// libScePosix follows FreeBSD's socket option numbers, which is where the constants come from. Apply
// reports setsockopt's return value rather than swallowing it: if the option is not accepted the
// protection is not in place, and that is worth being able to see (DevProbe's SOTEST command).

using SharpProspero.Interop.Posix;

namespace PayloadCommon;

internal static unsafe class ClientTimeout
{
    /// <summary>SO_RCVTIMEO. See the class comment on where the number comes from.</summary>
    public const int SoRcvTimeo = 0x1006;

    /// <summary>Seconds of silence after which a connected client is treated as gone.</summary>
    public const int Seconds = 60;

    /// <summary>setsockopt's answer for the most recent <see cref="Apply"/>, for a payload to report.</summary>
    public static int LastResult { get; private set; } = int.MinValue;

    /// <summary>
    /// Applies the receive timeout to an accepted connection. A struct timeval on 64-bit FreeBSD is two
    /// eight-byte fields, so sixteen bytes is what the option expects.
    /// </summary>
    public static int Apply(int client)
    {
        long* timeval = stackalloc long[2];
        timeval[0] = Seconds;
        timeval[1] = 0;
        LastResult = PosixSocket.setsockopt(client, PosixSocket.SolSocket, SoRcvTimeo, timeval, 16);
        return LastResult;
    }
}

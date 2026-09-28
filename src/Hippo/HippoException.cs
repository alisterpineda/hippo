namespace Hippo;

/// <summary>An error the user can act on. Commands print its message and exit 2.</summary>
internal sealed class HippoException(string message) : Exception(message);

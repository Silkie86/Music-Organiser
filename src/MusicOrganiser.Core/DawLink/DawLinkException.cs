namespace MusicOrganiser.Core.DawLink;

/// <summary>Talking to a DAW didn't work. The message says why, in words that can be shown as they are.</summary>
public sealed class DawLinkException(string message, Exception? inner = null) : Exception(message, inner);

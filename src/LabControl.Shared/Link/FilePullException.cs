namespace LabControl.Shared.Link;

/// <summary>A <c>PullFile</c> that did not produce a verified file; the message is for the teacher.</summary>
public sealed class FilePullException(string message) : Exception(message);

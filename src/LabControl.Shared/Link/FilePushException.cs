namespace LabControl.Shared.Link;

/// <summary>An upload that did not receive a verified completion from the console.</summary>
public sealed class FilePushException(string message) : Exception(message);

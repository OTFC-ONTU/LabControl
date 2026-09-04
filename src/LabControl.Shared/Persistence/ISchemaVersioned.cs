namespace LabControl.Shared.Persistence;

/// <summary>
/// Every file LabControl persists starts with a <c>schema_version</c> (D-20). A document
/// that does not carry one cannot be given one later without guessing which files predate
/// the change, so the interface is mandatory rather than a convention.
/// </summary>
public interface ISchemaVersioned
{
    /// <summary>The version the document on disk was written with; set on load and on save.</summary>
    int SchemaVersion { get; set; }
}

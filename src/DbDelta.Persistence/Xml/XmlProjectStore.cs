#pragma warning disable IDE0005
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
#pragma warning restore IDE0005
using DbDelta.Core.Abstractions;
using DbDelta.Core.Options;

namespace DbDelta.Persistence.Xml;

/// <summary>
/// Reads / writes the canonical <c>.dbd</c> XML project file.
/// </summary>
public sealed class XmlProjectStore : IProjectStore
{
    private const string Namespace = "https://schemas.dbdelta.org/project/v1";

    public async Task<DbDeltaProject> LoadAsync(string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream fs = File.OpenRead(filePath);
        XmlSerializer serializer = new(typeof(Surrogate), defaultNamespace: Namespace);
        Surrogate? doc = (Surrogate?)serializer.Deserialize(fs)
            ?? throw new InvalidDataException($"'{filePath}' is not a valid DbDelta project file.");
        return new DbDeltaProject(
            Name: doc.Name ?? "",
            SourceConnectionId: doc.SourceConnectionId,
            TargetConnectionId: doc.TargetConnectionId,
            Options: doc.Options,
            SelectedObjects: doc.SelectedObjects);
    }

    public async Task SaveAsync(string filePath, DbDeltaProject project, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(project);
        Surrogate doc = new()
        {
            Name = project.Name,
            SourceConnectionId = project.SourceConnectionId,
            TargetConnectionId = project.TargetConnectionId,
            Options = project.Options,
            SelectedObjects = project.SelectedObjects?.ToArray(),
        };
        XmlSerializer serializer = new(typeof(Surrogate), defaultNamespace: Namespace);
        XmlSerializerNamespaces ns = new();
        ns.Add(string.Empty, Namespace);
        XmlWriterSettings settings = new()
        {
            Async = true,
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        await using FileStream fs = File.Create(filePath);
        await using var writer = XmlWriter.Create(fs, settings);
        serializer.Serialize(writer, doc, ns);
        await writer.FlushAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    [XmlRoot("DbDeltaProject", Namespace = Namespace)]
    public sealed class Surrogate
    {
        [XmlAttribute("name")]
        public string? Name { get; set; }
        public Guid SourceConnectionId { get; set; }
        public Guid TargetConnectionId { get; set; }
        public ComparisonOptions Options { get; set; }
        [XmlArrayItem("Item")]
        public string[]? SelectedObjects { get; set; }
    }
}

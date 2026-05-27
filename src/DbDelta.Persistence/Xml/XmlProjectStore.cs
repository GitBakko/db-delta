#pragma warning disable IDE0005
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
#pragma warning restore IDE0005
using DbDelta.Core.Abstractions;
using DbDelta.Core.Options;

namespace DbDelta.Persistence.Xml;

/// <summary>
/// Reads / writes the canonical <c>.dbd</c> XML project file.
/// Supports reading schema v1 (legacy) and writing / reading schema v2.
/// </summary>
public sealed class XmlProjectStore : IProjectStore
{
    /// <summary>XML namespace used for both schema versions.</summary>
    private const string Namespace = "https://schemas.dbdelta.org/project/v1";

    // ── Public contract ────────────────────────────────────────────────────

    public async Task<DbDeltaProject> LoadAsync(string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string xml = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        XElement root = doc.Root
            ?? throw new InvalidDataException($"'{filePath}' is not a valid DbDelta project file.");

        string? schemaAttr = (string?)root.Attribute("schema");
        int schemaVersion = int.TryParse(schemaAttr, out int v) ? v : 1;

        return schemaVersion >= 2
            ? ParseV2(root)
            : ParseV1Legacy(root, filePath);
    }

    public async Task SaveAsync(string filePath, DbDeltaProject project, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(project);

        XDocument doc = BuildV2Document(project);

        string dir = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for '{filePath}'.");
        string tmp = Path.Combine(dir, Path.GetRandomFileName());

        try
        {
            XmlWriterSettings settings = new()
            {
                Async = true,
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                NewLineHandling = NewLineHandling.Replace,
            };
            await using (FileStream fs = File.Create(tmp))
            await using (var writer = XmlWriter.Create(fs, settings))
            {
                doc.WriteTo(writer);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(tmp, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    // ── V2 write ───────────────────────────────────────────────────────────

    private static XDocument BuildV2Document(DbDeltaProject p)
    {
        XNamespace ns = Namespace;

        XElement root = new(
            ns + "DbDeltaProject",
            new XAttribute("schema", "2"),
            new XElement(ns + "Name", p.Name),
            new XElement(ns + "CreatedUtc", FormatDateTime(p.CreatedUtc)),
            new XElement(ns + "LastModifiedUtc", FormatDateTime(p.LastModifiedUtc)));

        root.Add(BuildEndpointElement(ns, "Source", p.Source));
        root.Add(BuildEndpointElement(ns, "Target", p.Target));

        XElement ownerMappings = new(ns + "OwnerMappings");
        foreach (OwnerMappingEntry om in p.OwnerMappings)
        {
            ownerMappings.Add(new XElement(
                ns + "Map",
                new XAttribute("source", om.SourceOwner),
                new XAttribute("target", om.TargetOwner)));
        }

        root.Add(ownerMappings);

        XElement tableMappings = new(ns + "TableMappings");
        foreach (TableMappingEntry tm in p.TableMappings)
        {
            tableMappings.Add(new XElement(
                ns + "Map",
                new XAttribute("sourceSchema", tm.SourceSchema),
                new XAttribute("sourceName", tm.SourceName),
                new XAttribute("targetSchema", tm.TargetSchema),
                new XAttribute("targetName", tm.TargetName)));
        }

        root.Add(tableMappings);

        ProjectOptions opts = p.ProjectOptions;
        root.Add(new XElement(
            ns + "Options",
            new XAttribute("ignoreFillFactor", BoolToString(opts.IgnoreFillFactor)),
            new XAttribute("ignoreCollation", BoolToString(opts.IgnoreCollation)),
            new XAttribute("ignoreWhitespace", BoolToString(opts.IgnoreWhitespace)),
            new XAttribute("ignoreCommentBlocks", BoolToString(opts.IgnoreCommentBlocks)),
            new XAttribute(
                "treatExtendedPropertiesAsObjects",
                BoolToString(opts.TreatExtendedPropertiesAsObjects))));

        XElement selections = new(ns + "Selections");
        foreach (KeyValuePair<ObjectSelectionKey, bool> kv in p.Selections)
        {
            selections.Add(new XElement(
                ns + "Entry",
                new XAttribute("type", kv.Key.ObjectType),
                new XAttribute("schema", kv.Key.SchemaName),
                new XAttribute("name", kv.Key.ObjectName),
                new XAttribute("selected", BoolToString(kv.Value))));
        }

        root.Add(selections);

        // Legacy connection IDs and options — retained for backwards-compatible
        // round-trips when callers populate SourceConnectionId / TargetConnectionId.
        if (p.SourceConnectionId != Guid.Empty || p.TargetConnectionId != Guid.Empty)
        {
            root.Add(new XElement(
                ns + "LegacyRefs",
                new XAttribute("sourceId", p.SourceConnectionId.ToString("D")),
                new XAttribute("targetId", p.TargetConnectionId.ToString("D")),
                new XAttribute("options", ((int)p.Options).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        if (p.SelectedObjects is { Count: > 0 })
        {
            XElement selectedObjects = new(ns + "SelectedObjects");
            foreach (string obj in p.SelectedObjects)
            {
                selectedObjects.Add(new XElement(ns + "Item", obj));
            }

            root.Add(selectedObjects);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement BuildEndpointElement(XNamespace ns, string tagName, ProjectEndpoint? ep)
    {
        XElement el = new(ns + tagName);
        // If the endpoint is null we emit the element with no children.
        if (ep is null)
        {
            return el;
        }

        ProjectConnectionRef conn = ep.Connection;
        el.Add(new XElement(
            ns + "Connection",
            new XAttribute("id", conn.Id.ToString("D")),
            new XAttribute("name", conn.Name),
            new XAttribute("server", conn.ServerName),
            new XAttribute("database", conn.DatabaseName),
            new XAttribute("envTag", conn.EnvironmentTag),
            new XAttribute("envColor", conn.EnvironmentColorHex)));

        ProjectAuthentication auth = ep.Authentication;
        el.Add(new XElement(
            ns + "Authentication",
            new XAttribute("mode", auth.Mode.ToString()),
            new XAttribute("user", auth.UserName ?? string.Empty),
            new XAttribute("remember", BoolToString(auth.RememberCredentials)),
            new XAttribute("encrypt", BoolToString(auth.Encrypt)),
            new XAttribute("trust", BoolToString(auth.TrustServerCertificate))));

        return el;
    }

    // ── V2 read ────────────────────────────────────────────────────────────

    private static DbDeltaProject ParseV2(XElement root)
    {
        XNamespace ns = Namespace;

        string name = (string?)root.Element(ns + "Name") ?? string.Empty;
        DateTime created = ParseDateTime((string?)root.Element(ns + "CreatedUtc"));
        DateTime modified = ParseDateTime((string?)root.Element(ns + "LastModifiedUtc"));

        ProjectEndpoint? source = ParseEndpoint(root.Element(ns + "Source"), ns);
        ProjectEndpoint? target = ParseEndpoint(root.Element(ns + "Target"), ns);

        List<OwnerMappingEntry> ownerMappings = [];
        foreach (XElement map in root.Element(ns + "OwnerMappings")?.Elements(ns + "Map") ?? [])
        {
            ownerMappings.Add(new OwnerMappingEntry(
                (string?)map.Attribute("source") ?? string.Empty,
                (string?)map.Attribute("target") ?? string.Empty));
        }

        List<TableMappingEntry> tableMappings = [];
        foreach (XElement map in root.Element(ns + "TableMappings")?.Elements(ns + "Map") ?? [])
        {
            tableMappings.Add(new TableMappingEntry(
                (string?)map.Attribute("sourceSchema") ?? string.Empty,
                (string?)map.Attribute("sourceName") ?? string.Empty,
                (string?)map.Attribute("targetSchema") ?? string.Empty,
                (string?)map.Attribute("targetName") ?? string.Empty));
        }

        ProjectOptions opts = ProjectOptions.Default;
        XElement? optsEl = root.Element(ns + "Options");
        if (optsEl is not null)
        {
            opts = new ProjectOptions(
                IgnoreFillFactor: ParseBool(optsEl.Attribute("ignoreFillFactor")),
                IgnoreCollation: ParseBool(optsEl.Attribute("ignoreCollation")),
                IgnoreWhitespace: ParseBool(optsEl.Attribute("ignoreWhitespace")),
                IgnoreCommentBlocks: ParseBool(optsEl.Attribute("ignoreCommentBlocks")),
                TreatExtendedPropertiesAsObjects: ParseBool(
                    optsEl.Attribute("treatExtendedPropertiesAsObjects")));
        }

        Dictionary<ObjectSelectionKey, bool> selections = [];
        foreach (XElement entry in root.Element(ns + "Selections")?.Elements(ns + "Entry") ?? [])
        {
            ObjectSelectionKey key = new(
                (string?)entry.Attribute("type") ?? string.Empty,
                (string?)entry.Attribute("schema") ?? string.Empty,
                (string?)entry.Attribute("name") ?? string.Empty);
            selections[key] = ParseBool(entry.Attribute("selected"));
        }

        // Read legacy refs if present.
        Guid srcId = Guid.Empty;
        Guid tgtId = Guid.Empty;
        ComparisonOptions legacyOptions = ComparisonOptions.Default;
        XElement? legacyEl = root.Element(ns + "LegacyRefs");
        if (legacyEl is not null)
        {
            Guid.TryParse((string?)legacyEl.Attribute("sourceId"), out srcId);
            Guid.TryParse((string?)legacyEl.Attribute("targetId"), out tgtId);
            if (int.TryParse(
                (string?)legacyEl.Attribute("options"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int optInt))
            {
                legacyOptions = (ComparisonOptions)optInt;
            }
        }

        // Read legacy SelectedObjects if present.
        List<string>? selectedObjects = null;
        XElement? selectedObjectsEl = root.Element(ns + "SelectedObjects");
        if (selectedObjectsEl is not null)
        {
            selectedObjects = [.. selectedObjectsEl.Elements(ns + "Item").Select(e => e.Value)];
        }

        return new DbDeltaProject(
            Name: name,
            CreatedUtc: created,
            LastModifiedUtc: modified,
            Source: source,
            Target: target,
            OwnerMappings: ownerMappings,
            TableMappings: tableMappings,
            ProjectOptions: opts,
            Selections: selections,
            SourceConnectionId: srcId,
            TargetConnectionId: tgtId,
            Options: legacyOptions,
            SelectedObjects: selectedObjects);
    }

    private static ProjectEndpoint? ParseEndpoint(XElement? el, XNamespace ns)
    {
        if (el is null)
        {
            return null;
        }

        XElement? connEl = el.Element(ns + "Connection");
        XElement? authEl = el.Element(ns + "Authentication");
        if (connEl is null || authEl is null)
        {
            return null;
        }

        ProjectConnectionRef conn = new(
            Id: Guid.TryParse((string?)connEl.Attribute("id"), out Guid gid) ? gid : Guid.Empty,
            Name: (string?)connEl.Attribute("name") ?? string.Empty,
            ServerName: (string?)connEl.Attribute("server") ?? string.Empty,
            DatabaseName: (string?)connEl.Attribute("database") ?? string.Empty,
            EnvironmentTag: (string?)connEl.Attribute("envTag") ?? string.Empty,
            EnvironmentColorHex: (string?)connEl.Attribute("envColor") ?? string.Empty);

        string modeStr = (string?)authEl.Attribute("mode") ?? nameof(AuthenticationMode.SqlServer);
        AuthenticationMode mode = Enum.TryParse(modeStr, out AuthenticationMode m)
            ? m
            : AuthenticationMode.SqlServer;

        ProjectAuthentication auth = new(
            Mode: mode,
            UserName: (string?)authEl.Attribute("user"),
            RememberCredentials: ParseBool(authEl.Attribute("remember")),
            Encrypt: ParseBool(authEl.Attribute("encrypt")),
            TrustServerCertificate: ParseBool(authEl.Attribute("trust")));

        return new ProjectEndpoint(conn, auth);
    }

    // ── V1 read (legacy) ───────────────────────────────────────────────────

    private static DbDeltaProject ParseV1Legacy(XElement root, string filePath)
    {
        XmlSerializer serializer = new(typeof(V1Surrogate), defaultNamespace: Namespace);
        V1Surrogate? doc;
        using (XmlReader reader = root.CreateReader())
        {
            doc = (V1Surrogate?)serializer.Deserialize(reader)
                ?? throw new InvalidDataException(
                    $"'{filePath}' is not a valid DbDelta v1 project file.");
        }

        return new DbDeltaProject(
            Name: doc.Name ?? string.Empty,
            CreatedUtc: DateTime.UtcNow,
            LastModifiedUtc: DateTime.UtcNow,
            Source: null,
            Target: null,
            OwnerMappings: [],
            TableMappings: [],
            ProjectOptions: ProjectOptions.Default,
            Selections: new Dictionary<ObjectSelectionKey, bool>(),
            SourceConnectionId: doc.SourceConnectionId,
            TargetConnectionId: doc.TargetConnectionId,
            Options: doc.Options,
            SelectedObjects: doc.SelectedObjects);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string FormatDateTime(DateTime dt) =>
        dt.ToUniversalTime().ToString(
            "yyyy-MM-ddTHH:mm:ssZ",
            System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime ParseDateTime(string? value) =>
        DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTime dt)
            ? dt
            : DateTime.UtcNow;

    private static string BoolToString(bool value) => value ? "true" : "false";

    private static bool ParseBool(XAttribute? attr) =>
        attr is not null
        && string.Equals(attr.Value, "true", StringComparison.OrdinalIgnoreCase);

    // ── V1 surrogate (XmlSerializer) ───────────────────────────────────────

    [XmlRoot("DbDeltaProject", Namespace = Namespace)]
    public sealed class V1Surrogate
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
